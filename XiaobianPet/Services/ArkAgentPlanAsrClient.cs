using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace XiaobianPet.Services;

internal sealed class ArkAgentPlanAsrClient
{
    private const string ResourceId = "volc.seedasr.sauc.duration";
    private const int AudioChunkBytes = 6400;
    private const int MaximumAudioBytes = 16 * 1024 * 1024;
    private const int MaximumResponseBytes = 4 * 1024 * 1024;

    private static readonly Uri Endpoint = new(
        "wss://openspeech.bytedance.com/api/v3/plan/sauc/bigmodel_nostream");

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(18);

    internal string BackendDescription => "豆包 Seed-ASR 2.0 · Agent Plan";

    internal string? LastLogId { get; private set; }

    internal async Task<string> TranscribeAsync(
        string apiKey,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        LastLogId = null;
        ValidateApiKey(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        var fullPath = Path.GetFullPath(audioPath);
        var audio = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (audio.Length is <= 44 or > MaximumAudioBytes || !LooksLikePcmWave(audio))
        {
            throw new InvalidDataException("录音不是受支持的 16 kHz 单声道 PCM WAV 文件。");
        }

        using var socket = new ClientWebSocket();
        var requestId = Guid.NewGuid().ToString();
        socket.Options.SetRequestHeader("X-Api-Key", apiKey.Trim());
        socket.Options.SetRequestHeader("X-Api-Resource-Id", ResourceId);
        socket.Options.SetRequestHeader("X-Api-Request-Id", requestId);
        socket.Options.SetRequestHeader("X-Api-Connect-Id", requestId);
        socket.Options.SetRequestHeader("X-Api-Sequence", "-1");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.CollectHttpResponseDetails = true;
        var connected = false;

        try
        {
            using (var connectTimeout =
                   CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(Endpoint, connectTimeout.Token).ConfigureAwait(false);
            }

            connected = true;
            LastLogId = TryReadLogId(socket.HttpResponseHeaders);
            using var recognitionTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            recognitionTimeout.CancelAfter(RecognitionTimeout);
            var token = recognitionTimeout.Token;
            var initialPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                user = new { uid = "xiaobian_pet" },
                audio = new
                {
                    format = "wav",
                    codec = "raw",
                    rate = 16000,
                    bits = 16,
                    channel = 1
                },
                request = new
                {
                    model_name = "bigmodel",
                    enable_itn = true,
                    enable_punc = true,
                    enable_ddc = true,
                    show_utterances = true,
                    enable_nonstream = false
                }
            });
            var sequence = 1;
            await SendFrameAsync(
                socket,
                BuildClientFrame(0x11, sequence, initialPayload),
                token).ConfigureAwait(false);

            for (var offset = 0; offset < audio.Length; offset += AudioChunkBytes)
            {
                token.ThrowIfCancellationRequested();
                var length = Math.Min(AudioChunkBytes, audio.Length - offset);
                var chunk = audio.AsSpan(offset, length).ToArray();
                sequence++;
                var isFinal = offset + length >= audio.Length;
                var frameSequence = isFinal ? -sequence : sequence;
                var flags = isFinal ? (byte)0x23 : (byte)0x21;
                try
                {
                    await SendFrameAsync(
                        socket,
                        BuildClientFrame(flags, frameSequence, chunk),
                        token).ConfigureAwait(false);
                }
                finally
                {
                    Array.Clear(chunk);
                }
            }

            string? latestText = null;
            while (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var message = await ReceiveMessageAsync(socket, token).ConfigureAwait(false);
                var parsed = ParseServerFrame(message);
                if (parsed.ErrorCode is { } errorCode && errorCode != 0)
                {
                    throw new ArkAgentPlanAsrException(
                        $"方舟语音识别服务错误 {errorCode}。",
                        logId: LastLogId);
                }

                if (!string.IsNullOrWhiteSpace(parsed.Text))
                {
                    latestText = parsed.Text;
                }

                if (parsed.IsFinal)
                {
                    latestText = NormalizeTranscript(latestText);
                    if (string.IsNullOrWhiteSpace(latestText))
                    {
                        throw new ArkAgentPlanAsrException(
                            "豆包没有听清这句话，再靠近麦克风一点试试吧。",
                            logId: LastLogId);
                    }

                    return latestText;
                }
            }

            throw new ArkAgentPlanAsrException(
                "方舟语音识别连接在最终结果前关闭了。",
                logId: LastLogId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LastLogId ??= TryReadLogId(socket.HttpResponseHeaders);
            var phase = connected ? "等待结果" : "连接";
            throw new TimeoutException(
                AppendLogId($"方舟语音识别{phase}超时，已准备切换本机 Whisper。", LastLogId));
        }
        catch (ArkAgentPlanAsrException exception)
            when (string.IsNullOrWhiteSpace(exception.LogId) &&
                  !string.IsNullOrWhiteSpace(LastLogId))
        {
            throw new ArkAgentPlanAsrException(exception.Message, exception, LastLogId);
        }
        catch (WebSocketException exception)
        {
            LastLogId ??= TryReadLogId(socket.HttpResponseHeaders);
            throw new ArkAgentPlanAsrException(
                "无法连接方舟语音识别服务。",
                exception,
                LastLogId);
        }
        finally
        {
            Array.Clear(audio);
        }
    }

    internal static byte[] BuildClientFrame(byte messageAndFlags, int sequence, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var compressed = Gzip(payload);
        try
        {
            var frame = new byte[12 + compressed.Length];
            frame[0] = 0x11;
            frame[1] = messageAndFlags;
            frame[2] = messageAndFlags == 0x11 ? (byte)0x11 : (byte)0x01;
            frame[3] = 0x00;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), sequence);
            BinaryPrimitives.WriteUInt32BigEndian(
                frame.AsSpan(8, 4),
                checked((uint)compressed.Length));
            compressed.CopyTo(frame, 12);
            return frame;
        }
        finally
        {
            Array.Clear(compressed);
        }
    }

    internal static ArkAsrServerFrame ParseServerFrame(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length < 4)
        {
            throw new ArkAgentPlanAsrException("方舟语音识别返回了过短的数据帧。");
        }

        var headerBytes = (frame[0] & 0x0F) * 4;
        if ((frame[0] >> 4) != 1 || headerBytes < 4 || headerBytes > frame.Length)
        {
            throw new ArkAgentPlanAsrException("方舟语音识别返回了无效的协议头。");
        }

        var messageType = frame[1] >> 4;
        var flags = frame[1] & 0x0F;
        var serialization = frame[2] >> 4;
        var compression = frame[2] & 0x0F;
        var cursor = headerBytes;
        if ((flags & 0x01) != 0)
        {
            RequireBytes(frame, cursor, 4);
            cursor += 4;
        }

        if ((flags & 0x04) != 0)
        {
            RequireBytes(frame, cursor, 4);
            cursor += 4;
        }

        uint? errorCode = null;
        if (messageType == 0x0F)
        {
            RequireBytes(frame, cursor, 4);
            errorCode = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(cursor, 4));
            cursor += 4;
        }
        else if (messageType != 0x09)
        {
            throw new ArkAgentPlanAsrException(
                $"方舟语音识别返回了未知消息类型 {messageType}。");
        }

        RequireBytes(frame, cursor, 4);
        var payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
            frame.AsSpan(cursor, 4)));
        cursor += 4;
        RequireBytes(frame, cursor, payloadLength);
        var wirePayload = frame.AsSpan(cursor, payloadLength).ToArray();
        var decodedPayload = wirePayload;
        try
        {
            if (compression == 1)
            {
                decodedPayload = Gunzip(wirePayload);
            }
            else if (compression != 0)
            {
                throw new ArkAgentPlanAsrException("方舟语音识别返回了不支持的压缩格式。");
            }

            string? text = null;
            if (serialization == 1 && decodedPayload.Length > 0)
            {
                using var json = JsonDocument.Parse(
                    decodedPayload,
                    new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("result", out var result) &&
                    result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("text", out var textNode) &&
                    textNode.ValueKind == JsonValueKind.String)
                {
                    text = textNode.GetString();
                }
            }
            else if (serialization is not 0 and not 1)
            {
                throw new ArkAgentPlanAsrException("方舟语音识别返回了不支持的序列化格式。");
            }

            return new ArkAsrServerFrame((flags & 0x02) != 0, errorCode, text);
        }
        catch (JsonException exception)
        {
            throw new ArkAgentPlanAsrException("方舟语音识别返回了无效 JSON。", exception);
        }
        finally
        {
            if (!ReferenceEquals(decodedPayload, wirePayload))
            {
                Array.Clear(decodedPayload);
            }

            Array.Clear(wirePayload);
        }
    }

    internal static string? TryReadLogId(
        IReadOnlyDictionary<string, IEnumerable<string>>? responseHeaders)
    {
        if (responseHeaders is null)
        {
            return null;
        }

        if (!responseHeaders.TryGetValue("X-Tt-Logid", out var values))
        {
            values = responseHeaders
                .FirstOrDefault(pair => string.Equals(
                    pair.Key,
                    "X-Tt-Logid",
                    StringComparison.OrdinalIgnoreCase))
                .Value;
        }

        return NormalizeLogId(values?.FirstOrDefault());
    }

    internal static string? NormalizeLogId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= 128 && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private static async Task SendFrameAsync(
        ClientWebSocket socket,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(
                frame,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(frame);
        }
    }

    private static async Task<byte[]> ReceiveMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new ArkAgentPlanAsrException("方舟语音识别连接提前关闭了。");
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new ArkAgentPlanAsrException("方舟语音识别返回了非二进制消息。");
            }

            if (message.Length + result.Count > MaximumResponseBytes)
            {
                throw new ArkAgentPlanAsrException("方舟语音识别响应超过安全大小限制。");
            }

            await message.WriteAsync(
                buffer.AsMemory(0, result.Count),
                cancellationToken).ConfigureAwait(false);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static byte[] Gzip(byte[] value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(value, 0, value.Length);
        }

        return output.ToArray();
    }

    private static byte[] Gunzip(byte[] value)
    {
        using var input = new MemoryStream(value, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        if (output.Length > MaximumResponseBytes)
        {
            throw new ArkAgentPlanAsrException("方舟语音识别解压后的响应过大。");
        }

        return output.ToArray();
    }

    private static bool LooksLikePcmWave(byte[] bytes)
    {
        if (bytes.Length < 44 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var validFormat = false;
        var hasAudioData = false;
        var cursor = 12;
        while (cursor <= bytes.Length - 8)
        {
            var chunkId = bytes.AsSpan(cursor, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4, 4));
            var dataStart = cursor + 8;
            if (chunkSize > int.MaxValue || dataStart > bytes.Length - (int)chunkSize)
            {
                return false;
            }

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16)
                {
                    return false;
                }

                var format = bytes.AsSpan(dataStart, (int)chunkSize);
                validFormat =
                    BinaryPrimitives.ReadUInt16LittleEndian(format[..2]) == 1 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(2, 2)) == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(format.Slice(4, 4)) == 16000 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(format.Slice(14, 2)) == 16;
            }
            else if (chunkId.SequenceEqual("data"u8) && chunkSize > 0)
            {
                hasAudioData = true;
            }

            var paddedSize = checked((int)chunkSize + ((int)chunkSize & 1));
            cursor = dataStart + paddedSize;
        }

        return validFormat && hasAudioData;
    }

    private static void RequireBytes(byte[] frame, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > frame.Length - count)
        {
            throw new ArkAgentPlanAsrException("方舟语音识别数据帧长度无效。");
        }
    }

    private static string NormalizeTranscript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            ' ',
            text.Replace('\r', ' ').Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string AppendLogId(string message, string? logId)
    {
        return string.IsNullOrWhiteSpace(logId)
            ? message
            : $"{message}（X-Tt-Logid: {logId}）";
    }

    private static void ValidateApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (apiKey.Length > 4096 || apiKey.Any(char.IsControl))
        {
            throw new ArgumentException("方舟 Agent Plan 凭据格式无效。", nameof(apiKey));
        }
    }
}

internal sealed record ArkAsrServerFrame(bool IsFinal, uint? ErrorCode, string? Text);

internal sealed class ArkAgentPlanAsrException : Exception
{
    public ArkAgentPlanAsrException(
        string message,
        Exception? innerException = null,
        string? logId = null)
        : base(message, innerException)
    {
        LogId = logId;
    }

    public string? LogId { get; }

    public override string ToString()
    {
        var details = base.ToString();
        return string.IsNullOrWhiteSpace(LogId)
            ? details
            : $"{details}{Environment.NewLine}X-Tt-Logid: {LogId}";
    }
}
