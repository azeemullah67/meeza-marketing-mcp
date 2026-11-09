using System.Net;
using Microsoft.Extensions.Logging;

namespace Meeza.Marketing.Shared.Utilities;

/// <summary>
/// Retries throttled (429) and transient (5xx, network) failures with exponential backoff and jitter,
/// honouring Retry-After when the server sends it. Only idempotent read requests are issued by this
/// server, so retries are safe. Request URLs are redacted before logging.
/// </summary>
public sealed class RetryHandler(ILogger<RetryHandler> logger) : DelegatingHandler
{
    public int MaxRetries { get; init; } = 4;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Buffer the body so it can be re-sent.
        byte[]? body = null;
        System.Net.Http.Headers.MediaTypeHeaderValue? contentType = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            contentType = request.Content.Headers.ContentType;
        }

        for (var attempt = 0; ; attempt++)
        {
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = contentType;
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, ct).ConfigureAwait(false);
                if (!ShouldRetry(response.StatusCode) || attempt >= MaxRetries) return response;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries)
            {
                logger.LogWarning("Network error calling {Url}: {Error}. Retrying.", Redactor.Redact(request.RequestUri?.GetLeftPart(UriPartial.Path)), Redactor.Redact(ex.Message));
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxRetries)
            {
                logger.LogWarning("Timeout calling {Url}. Retrying.", request.RequestUri?.GetLeftPart(UriPartial.Path));
            }

            var delay = ComputeDelay(attempt, response);
            if (response is not null)
            {
                logger.LogWarning("HTTP {Status} from {Url}; retry {Attempt}/{Max} in {Delay:0.0}s.",
                    (int)response.StatusCode, request.RequestUri?.GetLeftPart(UriPartial.Path), attempt + 1, MaxRetries, delay.TotalSeconds);
                response.Dispose();
            }
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetry(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500 && code != HttpStatusCode.NotImplemented;

    private TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return Clamp(delta);
        if (retryAfter?.Date is { } date) return Clamp(date - DateTimeOffset.UtcNow);

        var exp = BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble() * BaseDelay.TotalMilliseconds;
        return Clamp(TimeSpan.FromMilliseconds(exp + jitter));
    }

    private TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t > MaxDelay ? MaxDelay : t;
}
