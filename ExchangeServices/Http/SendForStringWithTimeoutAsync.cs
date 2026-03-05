using System.Net;

namespace ExchangeServices.Http;

public sealed record HttpStringResponse(
    HttpStatusCode StatusCode,
    string Body,
    string? CorrelationId);

public static class HttpClientTimeoutExtensions
{
    /// <summary>
    /// Sends an HTTP request and returns the response body as string.
    /// Returns null on timeout/network failure/cancel.
    /// </summary>
    public static async Task<HttpStringResponse?> SendForStringWithTimeoutAsync(
        this HttpClient http,
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (request is null) throw new ArgumentNullException(nameof(request));

        // linked token so caller cancel still works
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var resp = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeoutCts.Token);

            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

            // common correlation headers across exchanges
            string? corr = null;
            if (resp.Headers.TryGetValues("Request-Id", out var rid))
                corr = rid.FirstOrDefault();
            else if (resp.Headers.TryGetValues("X-Correlation-Id", out var cid))
                corr = cid.FirstOrDefault();

            return new HttpStringResponse(resp.StatusCode, body, corr);
        }
        catch (OperationCanceledException)
        {
            // covers timeouts + caller cancellation
            return null;
        }
        catch (HttpRequestException)
        {
            // DNS, connect, TLS, etc.
            return null;
        }
    }
}