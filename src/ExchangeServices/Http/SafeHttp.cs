using System.Net;

namespace ExchangeServices.Http;

public static class SafeHttp
{
    // NOT sealed so SafeHttpExtensions.Result can derive from it
    public record Result(
        HttpStatusCode Status,
        string Body,
        string? CorrelationId = null
    )
    {
        // Back-compat: some clients still use res.Value
        public string Value => Body;
    }

    /// <summary>
    /// Sends a request with a PER-ATTEMPT timeout (CancelAfter).
    /// Returns null on transport/timeout failures.
    /// Returns Result for any HTTP response (including non-2xx).
    /// </summary>
    public static async Task<Result?> SendForStringAsync(
        HttpClient http,
        HttpRequestMessage req,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero)
            timeout = TimeSpan.FromSeconds(10);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            using var resp = await http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token
            ).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

            string? corr = null;
            if (resp.Headers.TryGetValues("X-Correlation-Id", out var v1)) corr = v1.FirstOrDefault();
            else if (resp.Headers.TryGetValues("X-Request-Id", out var v2)) corr = v2.FirstOrDefault();
            else if (resp.Headers.TryGetValues("Request-Id", out var v3)) corr = v3.FirstOrDefault();

            return new Result(resp.StatusCode, body, corr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // per-attempt timeout
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}