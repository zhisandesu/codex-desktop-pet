using System.IO;
using System.Text.Json;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

internal sealed class CompanionSessionStore
{
    private const long MaximumSessionFileBytes = 16 * 1024;
    private const int MaximumRetiredThreads = 32;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    internal CompanionSessionStore()
        : this(Path.Combine(CompanionThreadIdentity.DataRoot, "dialogue-session.json"))
    {
    }

    internal CompanionSessionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    internal CompanionSessionSnapshot Load()
    {
        try
        {
            return ToSnapshot(ReadDocument());
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or ArgumentException)
        {
            return Empty();
        }
    }

    internal Task SaveAsync(
        string conversationThreadId,
        string model,
        string threadSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadSource);
        return MutateAsync(
            document =>
            {
                document.ConversationThreadId = conversationThreadId.Trim();
                document.Model = model.Trim();
                document.ThreadSource = threadSource.Trim();
            },
            cancellationToken);
    }

    internal Task RetireAsync(
        string? conversationThreadId,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            document =>
            {
                var retired = NormalizeIds(document.RetiredThreadIds);
                var pendingArchive = NormalizeIds(document.PendingArchiveThreadIds);
                AddRetired(retired, document.ConversationThreadId);
                AddRetired(retired, conversationThreadId);
                AddRetired(pendingArchive, document.ConversationThreadId);
                AddRetired(pendingArchive, conversationThreadId);
                document.RetiredThreadIds = retired.TakeLast(MaximumRetiredThreads).ToList();
                document.PendingArchiveThreadIds = pendingArchive
                    .TakeLast(MaximumRetiredThreads)
                    .ToList();
                document.ConversationThreadId = null;
                document.Model = string.Empty;
                document.ThreadSource = CompanionThreadIdentity.ConversationSource;
            },
            cancellationToken);

    internal Task CompleteRetirementAsync(
        string conversationThreadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationThreadId);
        return MutateAsync(
            document => document.PendingArchiveThreadIds = NormalizeIds(
                    document.PendingArchiveThreadIds)
                .Where(id => !string.Equals(
                    id,
                    conversationThreadId.Trim(),
                    StringComparison.Ordinal))
                .ToList(),
            cancellationToken);
    }

    internal Task ClearAsync(CancellationToken cancellationToken = default) =>
        RetireAsync(null, cancellationToken);

    private async Task MutateAsync(
        Action<CompanionSessionDocument> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = ReadDocument();
            mutation(document);
            document.SchemaVersion = 2;
            document.RetiredThreadIds = NormalizeIds(document.RetiredThreadIds)
                .TakeLast(MaximumRetiredThreads)
                .ToList();
            document.PendingArchiveThreadIds = NormalizeIds(document.PendingArchiveThreadIds)
                .Where(document.RetiredThreadIds.Contains)
                .TakeLast(MaximumRetiredThreads)
                .ToList();
            document.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private CompanionSessionDocument ReadDocument()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumSessionFileBytes)
        {
            return NewDocument();
        }

        var document = JsonSerializer.Deserialize<CompanionSessionDocument>(
            File.ReadAllText(_path),
            SerializerOptions);
        if (document is null || document.SchemaVersion is not 1 and not 2)
        {
            return NewDocument();
        }

        document.ConversationThreadId = NormalizeId(document.ConversationThreadId);
        document.Model = document.Model?.Trim() ?? string.Empty;
        document.ThreadSource = document.ThreadSource?.Trim() ?? string.Empty;
        document.RetiredThreadIds = document.SchemaVersion == 1
            ? []
            : NormalizeIds(document.RetiredThreadIds);
        document.PendingArchiveThreadIds = document.SchemaVersion == 1
            ? []
            : NormalizeIds(document.PendingArchiveThreadIds);
        return document;
    }

    private async Task WriteDocumentAsync(
        CompanionSessionDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("角色会话文件路径缺少目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
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
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaximumSessionFileBytes)
            {
                throw new InvalidDataException("角色会话文件超过安全大小限制。");
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
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

    private static CompanionSessionSnapshot ToSnapshot(CompanionSessionDocument document) => new(
        NormalizeId(document.ConversationThreadId),
        document.Model?.Trim() ?? string.Empty,
        document.ThreadSource?.Trim() ?? string.Empty,
        NormalizeIds(document.RetiredThreadIds),
        NormalizeIds(document.PendingArchiveThreadIds),
        document.UpdatedAt);

    private static CompanionSessionDocument NewDocument() => new()
    {
        SchemaVersion = 2,
        ConversationThreadId = null,
        Model = string.Empty,
        ThreadSource = CompanionThreadIdentity.ConversationSource,
        RetiredThreadIds = [],
        PendingArchiveThreadIds = [],
        UpdatedAt = DateTimeOffset.MinValue
    };

    private static CompanionSessionSnapshot Empty() => ToSnapshot(NewDocument());

    private static string? NormalizeId(string? value)
    {
        var id = value?.Trim();
        return !string.IsNullOrWhiteSpace(id) && Guid.TryParse(id, out _) ? id : null;
    }

    private static List<string> NormalizeIds(IEnumerable<string>? values) =>
        values?.Select(NormalizeId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

    private static void AddRetired(ICollection<string> retired, string? value)
    {
        var id = NormalizeId(value);
        if (id is not null && !retired.Contains(id, StringComparer.Ordinal))
        {
            retired.Add(id);
        }
    }
}
