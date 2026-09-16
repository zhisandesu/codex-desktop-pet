using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed class ConversationArchiveStore
{
    private const long MaximumArchiveBytes = 64L * 1024 * 1024;
    private const int MaximumEncryptedLineCharacters = 64 * 1024;

    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("XiaobianPet.ConversationArchive.v1");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _archivePath;

    public ConversationArchiveStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaobianPet",
            "conversation-history.dpapi.jsonl"))
    {
    }

    public ConversationArchiveStore(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        _archivePath = Path.GetFullPath(archivePath);
    }

    public string ArchivePath => _archivePath;

    public async Task<bool> AppendAsync(
        ConversationArchiveEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = null;
        byte[]? protectedBytes = null;
        try
        {
            var directory = Path.GetDirectoryName(_archivePath)
                ?? throw new InvalidOperationException("对话备份路径缺少目录。");
            Directory.CreateDirectory(directory);
            if (File.Exists(_archivePath) && new FileInfo(_archivePath).Length >= MaximumArchiveBytes)
            {
                return false;
            }

            plaintext = JsonSerializer.SerializeToUtf8Bytes(entry, SerializerOptions);
            protectedBytes = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            var encoded = Convert.ToBase64String(protectedBytes);
            if (encoded.Length > MaximumEncryptedLineCharacters)
            {
                return false;
            }

            await using var stream = new FileStream(
                _archivePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(encoded.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationArchiveEntry>> ReadRecentAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0 || !OperatingSystem.IsWindows())
        {
            return [];
        }

        count = Math.Min(count, 200);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_archivePath) || new FileInfo(_archivePath).Length > MaximumArchiveBytes)
            {
                return [];
            }

            var tail = new Queue<string>(count);
            foreach (var line in File.ReadLines(_archivePath, Encoding.UTF8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.Length > MaximumEncryptedLineCharacters)
                {
                    continue;
                }

                tail.Enqueue(trimmed);
                while (tail.Count > count)
                {
                    tail.Dequeue();
                }
            }

            var entries = new List<ConversationArchiveEntry>(tail.Count);
            foreach (var line in tail)
            {
                byte[]? protectedBytes = null;
                byte[]? plaintext = null;
                try
                {
                    protectedBytes = Convert.FromBase64String(line);
                    plaintext = ProtectedData.Unprotect(
                        protectedBytes,
                        OptionalEntropy,
                        DataProtectionScope.CurrentUser);
                    var entry = JsonSerializer.Deserialize<ConversationArchiveEntry>(
                        plaintext,
                        SerializerOptions);
                    if (entry is not null &&
                        !string.IsNullOrWhiteSpace(entry.UserText) &&
                        !string.IsNullOrWhiteSpace(entry.AssistantText))
                    {
                        entries.Add(entry);
                    }
                }
                catch (Exception exception) when (exception is FormatException
                                                  or CryptographicException
                                                  or JsonException
                                                  or NotSupportedException)
                {
                    // A damaged line must not prevent the remaining local history
                    // from being shown. It is never rewritten as plaintext.
                }
                finally
                {
                    if (plaintext is not null)
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }

                    if (protectedBytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(protectedBytes);
                    }
                }
            }

            return entries;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }
}
