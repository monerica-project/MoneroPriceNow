using System.Net;

namespace ExchangeServices.Http;

public static class HttpClientSafeExtensions
{
    /// <summary>
    /// Convenience wrapper around SafeHttp.SendForStringAsync.
    /// </summary>
    public static Task<SafeHttp.Result?> TrySendForStringAsync(
        this HttpClient http,
        HttpRequestMessage req,
        TimeSpan timeout,
        CancellationToken ct = default)
        => SafeHttp.SendForStringAsync(http, req, timeout, ct);

    /// <summary>
    /// Uses SafeHttp.SendForStringAsync() with per-attempt timeout and simple retry/backoff.
    /// retryCount = number of retries AFTER the first attempt (0 = one attempt total).
    ///
    /// IMPORTANT: requestFactory MUST create a NEW HttpRequestMessage each attempt.
    /// If POSTing, also create NEW HttpContent each attempt (don’t reuse StringContent).
    /// </summary>
    public static async Task<SafeHttp.Result?> SendForStringWithRetryAsync(
        this HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        int retryCount,
        CancellationToken ct = default)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (requestFactory is null) throw new ArgumentNullException(nameof(requestFactory));

        var attempts = Math.Max(1, retryCount + 1);

        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = requestFactory();

            var res = await SafeHttp.SendForStringAsync(http, req, timeout, ct).ConfigureAwait(false);

            // null = transport failure / timeout
            if (res is null)
            {
                if (i < attempts - 1)
                {
                    await Backoff(i, ct).ConfigureAwait(false);
                    continue;
                }
                return null;
            }

            // Retry only on transient HTTP codes
            if (ShouldRetry(res.Status) && i < attempts - 1)
            {
                await Backoff(i, ct).ConfigureAwait(false);
                continue;
            }

            return res;
        }

        return null;
    }

    private static bool ShouldRetry(HttpStatusCode code)
    {
        var n = (int)code;

        // 408 Request Timeout
        if (code == HttpStatusCode.RequestTimeout) return true;

        // 429 Too Many Requests
        if (n == 429) return true;

        // 5xx
        if (n >= 500 && n <= 599) return true;

        return false;
    }

    private static Task Backoff(int attemptIndex, CancellationToken ct)
    {
        // 250ms, 500ms, 1s, 2s, 4s (cap) + jitter
        var exp = 1 << Math.Min(attemptIndex, 4);
        var baseMs = 250 * exp;
        var jitter = Random.Shared.Next(0, 150);
        var ms = Math.Min(4000, baseMs + jitter);
        return Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
    }
}