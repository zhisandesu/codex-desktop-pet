using System.IO;
using System.Text.Json;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

internal sealed class ConversationContextStore
{
    private const long MaximumContextFileBytes = 8 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private string _currentEpoch;

    internal ConversationContextStore()
        : this(Path.Combine(CompanionThreadIdentity.DataRoot, "conversation-context.json"))
    {
    }

    internal ConversationContextStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        var loadedEpoch = LoadEpoch();
        _currentEpoch = loadedEpoch ?? Guid.NewGuid().ToString("N");
        if (loadedEpoch is null)
        {
            PersistInitialEpoch(_currentEpoch);
        }
    }

    internal string CurrentEpoch => Volatile.Read(ref _currentEpoch);

    internal async Task<string> RotateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var nextEpoch = Guid.NewGuid().ToString("N");
            await WriteAsync(nextEpoch, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _currentEpoch, nextEpoch);
            return nextEpoch;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? LoadEpoch()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumContextFileBytes)
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<ConversationContextDocument>(
                File.ReadAllText(_path),
                SerializerOptions);
            return document is { SchemaVersion: 1 } &&
                   Guid.TryParseExact(document.Epoch, "N", out _)
                ? document.Epoch
                : null;
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException
                                          or ArgumentException)
        {
            return null;
        }
    }

    private void PersistInitialEpoch(string epoch)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("对话上下文路径缺少目录。");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(CreateDocument(epoch), SerializerOptions));
                File.Move(temporaryPath, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            // A later destructive memory action calls RotateAsync and must persist
            // successfully before it is allowed to change active memory.
        }
    }

    private async Task WriteAsync(string epoch, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("对话上下文路径缺少目录。");
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
                    CreateDocument(epoch),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
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

    private static ConversationContextDocument CreateDocument(string epoch) => new()
    {
        SchemaVersion = 1,
        Epoch = epoch,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
