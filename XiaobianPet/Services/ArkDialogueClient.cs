using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XiaobianPet.Services;

internal sealed class ArkDialogueClient : IPetDialogueGenerator, IDisposable
{
    private const string DefaultModel = "doubao-seed-2-0-mini-260428";
    private const int MaximumResponseBytes = 1024 * 1024;

    private static readonly Uri DefaultEndpoint = new(
        "https://ark.cn-beijing.volces.com/api/v3/chat/completions");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);
    private static int s_authenticationDisabled;

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Uri? _endpoint;
    private readonly string _model;

    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private int _isDisposed;

    public ArkDialogueClient()
    {
        _endpoint = ResolveEndpoint();
        _model = ResolveModel();

        var handler = new HttpClientHandler
        {
            // Do not forward prompts across redirects. A redirect is treated as a
            // failed request and participates in the normal cooldown policy.
            AllowAutoRedirect = false
        };
        _httpClient = new HttpClient(handler)
        {
            // The linked token below bounds headers and the complete response body.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<string?> GenerateAsync(
        string systemPrompt,
        string userPayload,
        XiaobianPet.Models.PetDialogueChannel channel,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userPayload);

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);

        var enteredGate = false;
        try
        {
            await _requestGate.WaitAsync(lifetimeCancellation.Token).ConfigureAwait(false);
            enteredGate = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

            if (Volatile.Read(ref s_authenticationDisabled) != 0
                || IsCoolingDown()
                || _endpoint is null)
            {
                return null;
            }

            string? apiKey = null;
            try
            {
                apiKey = WindowsCredentialStore.GetArkModelApiKey();
                if (!IsSafeApiKey(apiKey))
                {
                    return null;
                }

                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    lifetimeCancellation.Token);
                requestCancellation.CancelAfter(RequestTimeout);

                using var request = CreateRequest(
                    apiKey!,
                    systemPrompt,
                    userPayload);
                using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        requestCancellation.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    DisableForAuthenticationFailure();
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    RegisterFailure();
                    return null;
                }

                var content = await ReadContentAsync(
                        response.Content,
                        requestCancellation.Token)
                    .ConfigureAwait(false);
                if (content is null)
                {
                    RegisterFailure();
                    return null;
                }

                RegisterSuccess();
                return content;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(ArkDialogueClient));
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _isDisposed) != 0)
            {
                throw;
            }
            catch
            {
                // Timeouts, transport failures, invalid responses, and other single-call
                // failures are deliberately silent. No prompt, response, or key is logged.
                RegisterFailure();
                return null;
            }
            finally
            {
                apiKey = null;
            }
        }
        finally
        {
            if (enteredGate)
            {
                _requestGate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        _httpClient.Dispose();

        // The semaphore and cancellation source intentionally remain allocated: an
        // in-flight call may still be unwinding through its finally block.
    }

    private HttpRequestMessage CreateRequest(
        string apiKey,
        string systemPrompt,
        string userPayload)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPayload }
            },
            thinking = new { type = "disabled" },
            stream = false,
            max_tokens = 96,
            temperature = 0.82,
            top_p = 0.9
        });

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<string?> ReadContentAsync(
        HttpContent responseContent,
        CancellationToken cancellationToken)
    {
        if (responseContent.Headers.ContentLength is > MaximumResponseBytes)
        {
            return null;
        }

        await responseContent
            .LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken)
            .ConfigureAwait(false);
        await using var responseStream = await responseContent
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
                responseStream,
                new JsonDocumentOptions { MaxDepth = 32 },
                cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];
        if (firstChoice.ValueKind != JsonValueKind.Object
            || !firstChoice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = content.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private bool IsCoolingDown()
    {
        if (_cooldownUntil == DateTimeOffset.MinValue)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow < _cooldownUntil)
        {
            return true;
        }

        _cooldownUntil = DateTimeOffset.MinValue;
        return false;
    }

    private void DisableForAuthenticationFailure()
    {
        Interlocked.Exchange(ref s_authenticationDisabled, 1);
        _consecutiveFailures = 0;
        _cooldownUntil = DateTimeOffset.MinValue;
    }

    private void RegisterFailure()
    {
        _consecutiveFailures++;
        if (_consecutiveFailures < 3)
        {
            return;
        }

        _consecutiveFailures = 0;
        _cooldownUntil = DateTimeOffset.UtcNow.Add(FailureCooldown);
    }

    private void RegisterSuccess()
    {
        _consecutiveFailures = 0;
        _cooldownUntil = DateTimeOffset.MinValue;
    }

    private static bool IsSafeApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096)
        {
            return false;
        }

        foreach (var character in apiKey)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static Uri? ResolveEndpoint()
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable(
            "XIAOBIAN_ARK_DIALOGUE_ENDPOINT",
            EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return DefaultEndpoint;
        }

        if (!Uri.TryCreate(configuredEndpoint.Trim(), UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                endpoint.Host,
                DefaultEndpoint.Host,
                StringComparison.OrdinalIgnoreCase)
            || !endpoint.IsDefaultPort
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            return null;
        }

        return endpoint;
    }

    private static string ResolveModel()
    {
        var configuredModel = Environment.GetEnvironmentVariable(
            "XIAOBIAN_ARK_DIALOGUE_MODEL",
            EnvironmentVariableTarget.Process);
        if (string.IsNullOrWhiteSpace(configuredModel))
        {
            return DefaultModel;
        }

        var model = configuredModel.Trim();
        if (model.Length > 256 || model.Any(char.IsControl))
        {
            return DefaultModel;
        }

        return model;
    }
}
