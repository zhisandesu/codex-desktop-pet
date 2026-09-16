using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace XiaobianPet.Services;

internal sealed class ArkAgentPlanTtsClient : IDisposable
{
    private const string ResourceId = "seed-tts-2.0";
    private const long StreamFinishedCode = 20000000;
    private const int MaximumAudioBytes = 64 * 1024 * 1024;

    private static readonly TimeSpan OverallSynthesisTimeout = TimeSpan.FromSeconds(30);

    private static readonly Uri Endpoint = new(
        "https://openspeech.bytedance.com/api/v3/plan/tts/unidirectional");

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public string? LastLogId { get; private set; }

    public async Task<byte[]> SynthesizeAsync(
        string apiKey,
        string text,
        string speaker,
        string? instruction,
        int speechRate,
        int pitch,
        CancellationToken cancellationToken)
    {
        LastLogId = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(speaker);
        ValidateTuning(speechRate, pitch);

        using var synthesisTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        synthesisTimeout.CancelAfter(OverallSynthesisTimeout);
        var effectiveCancellationToken = synthesisTimeout.Token;

        var audioParameters = new Dictionary<string, object?>
        {
            ["format"] = "mp3",
            ["sample_rate"] = 24000
        };
        if (speechRate != 0)
        {
            audioParameters["speech_rate"] = speechRate;
        }

        var requestParameters = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["speaker"] = speaker,
            ["audio_params"] = audioParameters
        };

        var additions = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            additions["context_texts"] = new[] { instruction.Trim() };
        }

        if (pitch != 0)
        {
            additions["post_process"] = new Dictionary<string, object?>
            {
                ["pitch"] = pitch
            };
        }

        if (additions.Count > 0)
        {
            // Agent Plan expects additions as JSON encoded inside a JSON string.
            requestParameters["additions"] = JsonSerializer.Serialize(additions);
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["req_params"] = requestParameters
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        request.Headers.TryAddWithoutValidation("X-Api-Resource-Id", ResourceId);
        request.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString());

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            effectiveCancellationToken).ConfigureAwait(false);

        var logId = TryGetHeader(response, "X-Tt-Logid");
        LastLogId = logId;
        if (!response.IsSuccessStatusCode)
        {
            throw new ArkAgentPlanTtsException(
                $"方舟 TTS HTTP 请求失败（{(int)response.StatusCode} {response.StatusCode}）。",
                logId,
                response.StatusCode);
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(effectiveCancellationToken)
            .ConfigureAwait(false);
        var audio = await ReadAudioStreamAsync(
                responseStream,
                logId,
                response.StatusCode,
                effectiveCancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(logId))
        {
            Debug.WriteLine($"Ark Agent Plan TTS succeeded (log id {logId}).");
        }

        return audio;
    }

    public void Dispose() => _httpClient.Dispose();

    internal static async Task<byte[]> ReadAudioStreamAsync(
        Stream responseStream,
        string? logId,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(responseStream);

        using var reader = new StreamReader(
            responseStream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        using var audio = new MemoryStream();
        var streamFinished = false;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                line = line[5..].Trim();
            }

            if (line.Length == 0 || string.Equals(line, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonDocument message;
            try
            {
                message = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new ArkAgentPlanTtsException(
                        "方舟 TTS 返回了无法解析的流消息。",
                        logId,
                        statusCode,
                        exception);
            }

            using (message)
            {
                var root = message.RootElement;
                if (!TryReadCode(root, out var code))
                {
                    throw new ArkAgentPlanTtsException(
                        "方舟 TTS 返回了缺失或无效的状态码。",
                        logId,
                        statusCode);
                }

                if (code == StreamFinishedCode)
                {
                    streamFinished = true;
                    break;
                }

                if (code != 0)
                {
                    throw new ArkAgentPlanTtsException(
                        $"方舟 TTS 服务错误 {code}。",
                        logId,
                        statusCode);
                }

                if (!root.TryGetProperty("data", out var dataElement)
                    || dataElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var encodedChunk = dataElement.GetString();
                if (string.IsNullOrWhiteSpace(encodedChunk))
                {
                    continue;
                }

                byte[] chunk;
                try
                {
                    chunk = Convert.FromBase64String(encodedChunk);
                }
                catch (FormatException exception)
                {
                    throw new ArkAgentPlanTtsException(
                        "方舟 TTS 返回了无效的音频数据。",
                        logId,
                        statusCode,
                        exception);
                }

                if (audio.Length + chunk.Length > MaximumAudioBytes)
                {
                    throw new ArkAgentPlanTtsException(
                        "方舟 TTS 返回的音频超过安全大小限制。",
                        logId,
                        statusCode);
                }

                await audio.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!streamFinished)
        {
            throw new ArkAgentPlanTtsException(
                "方舟 TTS 音频流在完成标记前中断。",
                logId,
                statusCode);
        }

        if (audio.Length == 0)
        {
            throw new ArkAgentPlanTtsException(
                "方舟 TTS 返回成功，但没有收到音频数据。",
                logId,
                statusCode);
        }

        return audio.ToArray();
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        var value = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Header values are untrusted. Keep only a small printable value for diagnostics.
        var length = Math.Min(value.Length, 128);
        var sanitized = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            sanitized.Append(char.IsControl(value[index]) ? '_' : value[index]);
        }

        return sanitized.ToString();
    }

    private static bool TryReadCode(JsonElement root, out long code)
    {
        if (!root.TryGetProperty("code", out var codeElement))
        {
            code = 0;
            return false;
        }

        if (codeElement.ValueKind == JsonValueKind.Number
            && codeElement.TryGetInt64(out code))
        {
            return true;
        }

        if (codeElement.ValueKind == JsonValueKind.String
            && long.TryParse(
                codeElement.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out code))
        {
            return true;
        }

        code = 0;
        return false;
    }

    private static void ValidateTuning(int speechRate, int pitch)
    {
        if (speechRate is < -50 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speechRate),
                speechRate,
                "语速必须在 -50 到 100 之间。");
        }

        if (pitch is < -12 or > 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pitch),
                pitch,
                "音高必须在 -12 到 12 之间。");
        }
    }

}

internal sealed class ArkAgentPlanTtsException : Exception
{
    public ArkAgentPlanTtsException(
        string message,
        string? logId,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        LogId = logId;
        StatusCode = statusCode;
    }

    public string? LogId { get; }

    public HttpStatusCode? StatusCode { get; }
}
