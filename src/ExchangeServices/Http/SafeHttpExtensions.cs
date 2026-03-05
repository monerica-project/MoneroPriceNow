using System.Net;
using System.Text;

namespace ExchangeServices.Http;

public static class SafeHttpExtensions
{
    // This makes "Http.SafeHttpExtensions.Result" a real type with Status/Body
    public record Result(HttpStatusCode Status, string Body, string? CorrelationId = null)
        : SafeHttp.Result(Status, Body, CorrelationId);

    public static async Task<Result?> SendForStringAsync(
        HttpClient http,
        HttpRequestMessage req,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var r = await SafeHttp.SendForStringAsync(http, req, timeout, ct).ConfigureAwait(false);
        return r is null ? null : new Result(r.Status, r.Body, r.CorrelationId);
    }

    // Your solution is calling this in a few places
    public static StringContent JsonContent(string json)
        => new StringContent(json, Encoding.UTF8, "application/json");
}