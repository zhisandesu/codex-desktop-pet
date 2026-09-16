using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed class PetMemoryStore
{
    // 120 entries of 80 CJK characters can exceed 64 KiB because the default
    // JSON encoder escapes each character as \uXXXX. Keep the read ceiling well
    // above every document the store is willing to create, and enforce the same
    // ceiling before committing a write.
    private const long MaximumMemoryFileBytes = 512 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    private static readonly Regex[] SensitiveValuePatterns =
    [
        SensitiveRegex(@"-----BEGIN(?: [A-Z0-9]+)* PRIVATE KEY-----"),
        SensitiveRegex(@"\b(?:sk|rk|ark)-[a-z0-9][a-z0-9_-]{11,}\b"),
        SensitiveRegex(@"\b(?:ghp|gho|ghu|ghs|github_pat)_[a-z0-9_]{12,}\b"),
        SensitiveRegex(@"\bAKIA[0-9A-Z]{16}\b"),
        SensitiveRegex(@"\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\b"),
        SensitiveRegex(@"\bBearer\s+[a-z0-9._~+/=-]{8,}"),
        SensitiveRegex(
            @"(?:api[\s_-]*key|access[\s_-]*token|refresh[\s_-]*token|client[\s_-]*secret|password|passwd|pwd|secret|token|密钥|秘钥|密码|口令)\s*(?:是|为|[:：=])\s*['""]?[^\s,，;；]{4,}"),
        SensitiveRegex(@"(?:password|passwd|pwd)\s*=\s*[^\s;]{4,}"),
        SensitiveRegex(@"\b[a-z][a-z0-9+.-]*://[^\s/:]+:[^\s/@]+@")
    ];

    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly string _memoryPath;

    private List<PetMemoryEntry> _entries = [];
    private PetMemoryStoreState _state = PetMemoryStoreState.Ready;
    private int _schemaVersion = PetMemoryLimits.CurrentSchemaVersion;
    private string? _preservedCorruptPath;
    private string? _notice;

    public PetMemoryStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaobianPet",
            "memory.json"))
    {
    }

    public PetMemoryStore(string memoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryPath);
        _memoryPath = Path.GetFullPath(memoryPath);
        Load();
    }

    public string MemoryPath => _memoryPath;

    public int Count
    {
        get
        {
            lock (_stateGate)
            {
                return _entries.Count;
            }
        }
    }

    public PetMemorySnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return new PetMemorySnapshot(
                    _schemaVersion,
                    _state,
                    _entries.ToArray(),
                    _preservedCorruptPath,
                    _notice);
            }
        }
    }

    public IReadOnlyList<PetMemoryEntry> Find(string? query)
    {
        var snapshot = Snapshot;
        if (string.IsNullOrWhiteSpace(query))
        {
            return snapshot.Entries
                .OrderByDescending(entry => entry.UpdatedAt)
                .ToArray();
        }

        if (!TryNormalizeContent(query, out var normalizedQuery) ||
            CountUnicodeCharacters(normalizedQuery) > PetMemoryLimits.MaxContentCharacters)
        {
            return [];
        }

        return FindMatches(
            snapshot.Entries,
            normalizedQuery,
            includeQueryContainingEntry: true);
    }

    public async Task<PetMemoryMutationResult> RememberAsync(
        string? content,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeContent(content, out var normalizedContent))
        {
            return Result(PetMemoryMutationStatus.InvalidContent);
        }

        if (CountUnicodeCharacters(normalizedContent) > PetMemoryLimits.MaxContentCharacters)
        {
            return Result(PetMemoryMutationStatus.TooLong);
        }

        if (ContainsSensitiveValue(normalizedContent))
        {
            return Result(PetMemoryMutationStatus.SensitiveContent);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            if (snapshot.IsReadOnly)
            {
                return Result(PetMemoryMutationStatus.ReadOnly);
            }

            var duplicate = snapshot.Entries.FirstOrDefault(entry =>
                string.Equals(entry.Content, normalizedContent, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                return Result(PetMemoryMutationStatus.Duplicate, duplicate);
            }

            if (snapshot.Count >= PetMemoryLimits.MaxEntries)
            {
                return Result(PetMemoryMutationStatus.LimitReached);
            }

            var now = DateTimeOffset.UtcNow;
            var entry = new PetMemoryEntry(
                Guid.NewGuid().ToString("N"),
                normalizedContent,
                now,
                now,
                PetMemoryCategory.Other,
                4,
                PetMemoryOrigin.Explicit);
            var nextEntries = snapshot.Entries.Append(entry).ToList();
            if (!await TryPersistAsync(nextEntries, cancellationToken).ConfigureAwait(false))
            {
                return Result(PetMemoryMutationStatus.WriteFailed);
            }

            ReplaceEntries(nextEntries);
            return Result(PetMemoryMutationStatus.Remembered, entry);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<PetMemoryMutationResult> UpsertAutomaticAsync(
        PetAutomaticMemoryProposal proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!TryNormalizeContent(proposal.Content, out var normalizedContent))
        {
            return Result(PetMemoryMutationStatus.InvalidContent);
        }

        if (CountUnicodeCharacters(normalizedContent) > PetMemoryLimits.MaxContentCharacters)
        {
            return Result(PetMemoryMutationStatus.TooLong);
        }

        if (ContainsSensitiveValue(normalizedContent) ||
            proposal.Importance is < 1 or > 5 ||
            !Enum.IsDefined(proposal.Category))
        {
            return Result(PetMemoryMutationStatus.SensitiveContent);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            if (snapshot.IsReadOnly)
            {
                return Result(PetMemoryMutationStatus.ReadOnly);
            }

            var duplicate = snapshot.Entries.FirstOrDefault(entry =>
                string.Equals(entry.Content, normalizedContent, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                return Result(PetMemoryMutationStatus.Duplicate, duplicate);
            }

            var now = DateTimeOffset.UtcNow;
            if (string.Equals(proposal.Action, "update", StringComparison.OrdinalIgnoreCase))
            {
                var target = snapshot.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.Id, proposal.MemoryId, StringComparison.Ordinal));
                if (target is null || target.Origin == PetMemoryOrigin.Explicit)
                {
                    return Result(PetMemoryMutationStatus.NotFound);
                }

                var updated = target with
                {
                    Content = normalizedContent,
                    UpdatedAt = now,
                    Category = proposal.Category,
                    Importance = proposal.Importance,
                    Origin = PetMemoryOrigin.Automatic
                };
                var nextEntries = snapshot.Entries
                    .Select(entry => string.Equals(entry.Id, target.Id, StringComparison.Ordinal)
                        ? updated
                        : entry)
                    .ToList();
                if (!await TryPersistAsync(nextEntries, cancellationToken).ConfigureAwait(false))
                {
                    return Result(PetMemoryMutationStatus.WriteFailed);
                }

                ReplaceEntries(nextEntries);
                return Result(PetMemoryMutationStatus.Updated, updated);
            }

            if (!string.Equals(proposal.Action, "add", StringComparison.OrdinalIgnoreCase))
            {
                return Result(PetMemoryMutationStatus.InvalidContent);
            }

            if (snapshot.Count >= PetMemoryLimits.MaxEntries)
            {
                return Result(PetMemoryMutationStatus.LimitReached);
            }

            var entry = new PetMemoryEntry(
                Guid.NewGuid().ToString("N"),
                normalizedContent,
                now,
                now,
                proposal.Category,
                proposal.Importance,
                PetMemoryOrigin.Automatic);
            var appended = snapshot.Entries.Append(entry).ToList();
            if (!await TryPersistAsync(appended, cancellationToken).ConfigureAwait(false))
            {
                return Result(PetMemoryMutationStatus.WriteFailed);
            }

            ReplaceEntries(appended);
            return Result(PetMemoryMutationStatus.Remembered, entry);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<PetMemoryMutationResult> ForgetAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeContent(query, out var normalizedQuery))
        {
            return Result(PetMemoryMutationStatus.InvalidContent);
        }

        if (CountUnicodeCharacters(normalizedQuery) > PetMemoryLimits.MaxContentCharacters)
        {
            return Result(PetMemoryMutationStatus.TooLong);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            if (snapshot.IsReadOnly)
            {
                return Result(PetMemoryMutationStatus.ReadOnly);
            }

            var matches = FindMatches(
                snapshot.Entries,
                normalizedQuery,
                includeQueryContainingEntry: false);
            if (matches.Count == 0)
            {
                return Result(PetMemoryMutationStatus.NotFound);
            }

            if (matches.Count > 1)
            {
                return Result(PetMemoryMutationStatus.Ambiguous, matches: matches);
            }

            var removed = matches[0];
            var nextEntries = snapshot.Entries
                .Where(entry => !string.Equals(entry.Id, removed.Id, StringComparison.Ordinal))
                .ToList();
            if (!await TryPersistAsync(nextEntries, cancellationToken).ConfigureAwait(false))
            {
                return Result(PetMemoryMutationStatus.WriteFailed);
            }

            ReplaceEntries(nextEntries);
            return Result(PetMemoryMutationStatus.Removed, removed);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<PetMemoryMutationResult> ClearAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            if (snapshot.IsReadOnly)
            {
                return Result(PetMemoryMutationStatus.ReadOnly);
            }

            if (snapshot.Count == 0)
            {
                return Result(PetMemoryMutationStatus.AlreadyEmpty);
            }

            if (!await TryPersistAsync([], cancellationToken).ConfigureAwait(false))
            {
                return Result(PetMemoryMutationStatus.WriteFailed);
            }

            ReplaceEntries([]);
            return Result(PetMemoryMutationStatus.Cleared);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_memoryPath))
        {
            return;
        }

        try
        {
            if (new FileInfo(_memoryPath).Length > MaximumMemoryFileBytes)
            {
                throw new InvalidDataException("记忆文件超过安全大小限制。");
            }

            var json = File.ReadAllText(_memoryPath);
            using var parsed = JsonDocument.Parse(json, DocumentOptions);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !TryReadSchemaVersion(parsed.RootElement, out var schemaVersion))
            {
                throw new InvalidDataException("记忆文件缺少有效的 schemaVersion。");
            }

            if (schemaVersion is not 1 && schemaVersion != PetMemoryLimits.CurrentSchemaVersion)
            {
                SetState(
                    schemaVersion,
                    PetMemoryStoreState.ReadOnlyUnknownSchema,
                    [],
                    null,
                    $"记忆文件版本 {schemaVersion} 暂不受支持，已用只读方式保护原文件。");
                return;
            }

            var document = JsonSerializer.Deserialize<PetMemoryDocument>(json, SerializerOptions);
            var validatedEntries = ValidateDocument(document, schemaVersion);
            SetState(
                PetMemoryLimits.CurrentSchemaVersion,
                PetMemoryStoreState.Ready,
                validatedEntries,
                null,
                null);
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidDataException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            PreserveCorruptFile();
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException)
        {
            SetState(
                PetMemoryLimits.CurrentSchemaVersion,
                PetMemoryStoreState.ReadOnlyUnavailable,
                [],
                null,
                "记忆文件暂时无法读取，已停用写入以保护原文件。");
        }
    }

    private static List<PetMemoryEntry> ValidateDocument(
        PetMemoryDocument? document,
        int sourceSchemaVersion)
    {
        if (document is null ||
            document.SchemaVersion != sourceSchemaVersion ||
            document.Entries is null ||
            document.Entries.Count > PetMemoryLimits.MaxEntries)
        {
            throw new InvalidDataException("记忆文件结构无效。");
        }

        var validated = new List<PetMemoryEntry>(document.Entries.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var contents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawEntry in document.Entries)
        {
            var category = sourceSchemaVersion == 1
                ? PetMemoryCategory.Other
                : rawEntry?.Category ?? PetMemoryCategory.Other;
            var importance = sourceSchemaVersion == 1 ? 3 : rawEntry?.Importance ?? 0;
            var origin = sourceSchemaVersion == 1
                ? PetMemoryOrigin.Explicit
                : rawEntry?.Origin ?? PetMemoryOrigin.Explicit;
            if (rawEntry is null ||
                !Guid.TryParse(rawEntry.Id, out _) ||
                !ids.Add(rawEntry.Id) ||
                rawEntry.CreatedAt == default ||
                rawEntry.UpdatedAt == default ||
                rawEntry.UpdatedAt < rawEntry.CreatedAt ||
                !TryNormalizeContent(rawEntry.Content, out var normalizedContent) ||
                CountUnicodeCharacters(normalizedContent) > PetMemoryLimits.MaxContentCharacters ||
                ContainsSensitiveValue(normalizedContent) ||
                !Enum.IsDefined(category) ||
                !Enum.IsDefined(origin) ||
                importance is < 1 or > 5 ||
                !contents.Add(normalizedContent))
            {
                throw new InvalidDataException("记忆文件包含无效条目。");
            }

            validated.Add(rawEntry with
            {
                Id = rawEntry.Id.Trim(),
                Content = normalizedContent,
                Category = category,
                Importance = importance,
                Origin = origin
            });
        }

        return validated;
    }

    private async Task<bool> TryPersistAsync(
        IReadOnlyList<PetMemoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_memoryPath)
            ?? throw new InvalidOperationException("记忆文件路径缺少目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_memoryPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new PetMemoryDocument
            {
                SchemaVersion = PetMemoryLimits.CurrentSchemaVersion,
                Entries = entries.ToList()
            };

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumMemoryFileBytes)
                {
                    return false;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _memoryPath, overwrite: true);
            temporaryPath = string.Empty;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private void PreserveCorruptFile()
    {
        if (!File.Exists(_memoryPath))
        {
            SetState(
                PetMemoryLimits.CurrentSchemaVersion,
                PetMemoryStoreState.Ready,
                [],
                null,
                null);
            return;
        }

        var directory = Path.GetDirectoryName(_memoryPath)
            ?? throw new InvalidOperationException("记忆文件路径缺少目录。");
        var corruptPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_memoryPath)}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.corrupt");

        try
        {
            File.Move(_memoryPath, corruptPath, overwrite: false);
            SetState(
                PetMemoryLimits.CurrentSchemaVersion,
                PetMemoryStoreState.Ready,
                [],
                corruptPath,
                "损坏的记忆文件已保留为 .corrupt；新的记忆库为空。");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            SetState(
                PetMemoryLimits.CurrentSchemaVersion,
                PetMemoryStoreState.ReadOnlyCorruptFile,
                [],
                null,
                "记忆文件已损坏且无法安全保留，已停用写入以避免覆盖原文件。");
        }
    }

    private static IReadOnlyList<PetMemoryEntry> FindMatches(
        IReadOnlyList<PetMemoryEntry> entries,
        string normalizedQuery,
        bool includeQueryContainingEntry)
    {
        var exact = entries
            .Where(entry => string.Equals(entry.Content, normalizedQuery, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.UpdatedAt)
            .ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        return entries
            .Where(entry =>
                entry.Content.Contains(normalizedQuery, StringComparison.Ordinal) ||
                includeQueryContainingEntry &&
                normalizedQuery.Contains(entry.Content, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.UpdatedAt)
            .ToArray();
    }

    internal static bool TryNormalizeContent(string? content, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            var compatibilityNormalized = content.Normalize(NormalizationForm.FormKC);
            var builder = new StringBuilder(compatibilityNormalized.Length);
            var pendingSpace = false;
            foreach (var rune in compatibilityNormalized.EnumerateRunes())
            {
                if (Rune.IsWhiteSpace(rune))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                var category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.Surrogate)
                {
                    return false;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(rune.ToString());
            }

            normalized = builder.ToString();
            return normalized.Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static int CountUnicodeCharacters(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    internal static bool ContainsSensitiveValue(string value)
    {
        foreach (var pattern in SensitiveValuePatterns)
        {
            try
            {
                if (pattern.IsMatch(value))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private static Regex SensitiveRegex(string pattern) => new(
        pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private static bool TryReadSchemaVersion(JsonElement root, out int schemaVersion)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out schemaVersion))
            {
                return true;
            }
        }

        schemaVersion = default;
        return false;
    }

    private void ReplaceEntries(List<PetMemoryEntry> entries)
    {
        lock (_stateGate)
        {
            _entries = entries;
            _schemaVersion = PetMemoryLimits.CurrentSchemaVersion;
            _state = PetMemoryStoreState.Ready;
        }
    }

    private void SetState(
        int schemaVersion,
        PetMemoryStoreState state,
        List<PetMemoryEntry> entries,
        string? preservedCorruptPath,
        string? notice)
    {
        lock (_stateGate)
        {
            _schemaVersion = schemaVersion;
            _state = state;
            _entries = entries;
            _preservedCorruptPath = preservedCorruptPath;
            _notice = notice;
        }
    }

    private static PetMemoryMutationResult Result(
        PetMemoryMutationStatus status,
        PetMemoryEntry? entry = null,
        IReadOnlyList<PetMemoryEntry>? matches = null) =>
        new(status, entry, matches ?? []);
}
