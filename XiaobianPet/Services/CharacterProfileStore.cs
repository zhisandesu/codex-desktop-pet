using System.IO;
using System.Text.Json;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed class CharacterProfileStore
{
    private const long MaximumProfileFileBytes = 32 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly string _profilePath;
    private List<string> _directives = [];
    private int _version = 1;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;
    private bool _readOnly;

    public CharacterProfileStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaobianPet",
            "character-profile.json"))
    {
    }

    public CharacterProfileStore(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        _profilePath = Path.GetFullPath(profilePath);
        Load();
    }

    public CharacterProfileSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return new CharacterProfileSnapshot(
                    CharacterProfileLimits.CurrentSchemaVersion,
                    _version,
                    _directives.ToArray(),
                    _updatedAt);
            }
        }
    }

    public async Task<CharacterProfileMutationResult> AddDirectiveAsync(
        string? directive,
        CancellationToken cancellationToken = default)
    {
        if (!PetMemoryStore.TryNormalizeContent(directive, out var normalized))
        {
            return new CharacterProfileMutationResult(CharacterProfileMutationStatus.InvalidContent);
        }

        if (PetMemoryStore.CountUnicodeCharacters(normalized) >
            CharacterProfileLimits.MaxDirectiveCharacters)
        {
            return new CharacterProfileMutationResult(CharacterProfileMutationStatus.TooLong);
        }

        if (PetMemoryStore.ContainsSensitiveValue(normalized))
        {
            return new CharacterProfileMutationResult(CharacterProfileMutationStatus.SensitiveContent);
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CharacterProfileSnapshot snapshot;
            lock (_stateGate)
            {
                if (_readOnly)
                {
                    return new CharacterProfileMutationResult(CharacterProfileMutationStatus.ReadOnly);
                }

                snapshot = Snapshot;
            }

            if (snapshot.OwnerDirectives.Contains(normalized, StringComparer.Ordinal))
            {
                return new CharacterProfileMutationResult(
                    CharacterProfileMutationStatus.Duplicate,
                    normalized);
            }

            if (snapshot.OwnerDirectives.Count >= CharacterProfileLimits.MaxDirectives)
            {
                return new CharacterProfileMutationResult(CharacterProfileMutationStatus.LimitReached);
            }

            var next = snapshot.OwnerDirectives.Append(normalized).ToList();
            var nextVersion = checked(snapshot.Version + 1);
            var now = DateTimeOffset.UtcNow;
            if (!await TryPersistAsync(next, nextVersion, now, cancellationToken).ConfigureAwait(false))
            {
                return new CharacterProfileMutationResult(CharacterProfileMutationStatus.WriteFailed);
            }

            ReplaceState(next, nextVersion, now);
            return new CharacterProfileMutationResult(
                CharacterProfileMutationStatus.Updated,
                normalized);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<CharacterProfileMutationResult> ClearAsync(
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            lock (_stateGate)
            {
                if (_readOnly)
                {
                    return new CharacterProfileMutationResult(CharacterProfileMutationStatus.ReadOnly);
                }
            }

            if (snapshot.OwnerDirectives.Count == 0)
            {
                return new CharacterProfileMutationResult(CharacterProfileMutationStatus.AlreadyEmpty);
            }

            var nextVersion = checked(snapshot.Version + 1);
            var now = DateTimeOffset.UtcNow;
            if (!await TryPersistAsync([], nextVersion, now, cancellationToken).ConfigureAwait(false))
            {
                return new CharacterProfileMutationResult(CharacterProfileMutationStatus.WriteFailed);
            }

            ReplaceState([], nextVersion, now);
            return new CharacterProfileMutationResult(CharacterProfileMutationStatus.Cleared);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private void Load()
    {
        if (!File.Exists(_profilePath))
        {
            return;
        }

        try
        {
            if (new FileInfo(_profilePath).Length > MaximumProfileFileBytes)
            {
                throw new InvalidDataException("角色设定文件超过安全大小限制。");
            }

            var document = JsonSerializer.Deserialize<CharacterProfileDocument>(
                File.ReadAllText(_profilePath),
                SerializerOptions);
            if (document is null ||
                document.SchemaVersion != CharacterProfileLimits.CurrentSchemaVersion ||
                document.Version < 1 ||
                document.OwnerDirectives is null ||
                document.OwnerDirectives.Count > CharacterProfileLimits.MaxDirectives)
            {
                throw new InvalidDataException("角色设定文件结构无效。");
            }

            var validated = new List<string>(document.OwnerDirectives.Count);
            foreach (var directive in document.OwnerDirectives)
            {
                if (!PetMemoryStore.TryNormalizeContent(directive, out var normalized) ||
                    PetMemoryStore.CountUnicodeCharacters(normalized) >
                    CharacterProfileLimits.MaxDirectiveCharacters ||
                    PetMemoryStore.ContainsSensitiveValue(normalized) ||
                    validated.Contains(normalized, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("角色设定文件包含无效内容。");
                }

                validated.Add(normalized);
            }

            ReplaceState(
                validated,
                document.Version,
                document.UpdatedAt == default ? DateTimeOffset.UtcNow : document.UpdatedAt);
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidDataException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            lock (_stateGate)
            {
                _readOnly = true;
                _directives = [];
                _version = 1;
            }
        }
    }

    private async Task<bool> TryPersistAsync(
        IReadOnlyList<string> directives,
        int version,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_profilePath)
            ?? throw new InvalidOperationException("角色设定文件路径缺少目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_profilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = new CharacterProfileDocument
            {
                SchemaVersion = CharacterProfileLimits.CurrentSchemaVersion,
                Version = version,
                OwnerDirectives = directives.ToList(),
                UpdatedAt = updatedAt
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
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _profilePath, overwrite: true);
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

    private void ReplaceState(List<string> directives, int version, DateTimeOffset updatedAt)
    {
        lock (_stateGate)
        {
            _directives = directives;
            _version = version;
            _updatedAt = updatedAt;
            _readOnly = false;
        }
    }
}
