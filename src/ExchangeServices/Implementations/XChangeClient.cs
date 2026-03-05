using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class XChangeClient : IXChangeClient
{
    private readonly HttpClient http;
    private readonly XChangeOptions opt;

    public string  ExchangeKey => "xchange";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;

    public XChangeClient(HttpClient http, IOptions<XChangeOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // SELL: 1 XMR -> USDT (quote per 1 base)
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        // xchange uses lowercase currency codes
        var from = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToLowerInvariant();
        var to = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return null;

        var url =
            $"/exchange/estimate" +
            $"?from_currency={Uri.EscapeDataString(from)}" +
            $"&to_currency={Uri.EscapeDataString(to)}" +
            $"&amount=1";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddDefaultHeaders(req);

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return null;

        var dto = JsonSerializer.Deserialize<EstimateDto>(
            raw,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString
            });

        if (dto?.Estimate is null || dto.Estimate <= 0)
            return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.Estimate.Value, // quote per 1 base
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // BUY: how much USDT needed to receive 1 XMR
    // We do: estimate XMR received for N USDT, then buy = N / xmrReceived.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var usdt = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToLowerInvariant();
        var xmr = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(usdt) || string.IsNullOrWhiteSpace(xmr))
            return null;

        // try a few amounts to avoid min limits / rounding issues
        var testAmounts = new[] { 10m, 25m, 50m, 100m, 250m };

        foreach (var amt in testAmounts)
        {
            var url =
                $"/exchange/estimate" +
                $"?from_currency={Uri.EscapeDataString(usdt)}" +
                $"&to_currency={Uri.EscapeDataString(xmr)}" +
                $"&amount={Uri.EscapeDataString(amt.ToString(System.Globalization.CultureInfo.InvariantCulture))}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddDefaultHeaders(req);

            HttpResponseMessage resp;
            string raw;

            try
            {
                resp = await http.SendAsync(req, ct);
                raw = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // timeout
                continue;
            }
            catch (HttpRequestException)
            {
                continue;
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    continue;

                EstimateDto? dto;
                try
                {
                    dto = JsonSerializer.Deserialize<EstimateDto>(
                        raw,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            NumberHandling = JsonNumberHandling.AllowReadingFromString
                        });
                }
                catch
                {
                    continue;
                }

                if (dto?.Estimate is null || dto.Estimate <= 0)
                    continue;

                var xmrReceived = dto.Estimate.Value;
                var usdtPerXmr = amt / xmrReceived;

                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: usdtPerXmr,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }
        }

        return null;
    }

    // currencies: simple (no networks shown in docs)
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var from = await GetCurrencyListAsync("/currencies/from", ct);
        var to = await GetCurrencyListAsync("/currencies/to", ct);

        // union
        return from
            .Union(to, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ExchangeCurrency(
                ExchangeId: x.ToLowerInvariant(),
                Ticker: x.ToUpperInvariant(),
                Network: "Mainnet"
            ))
            .OrderBy(x => x.Ticker)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetCurrencyListAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        AddDefaultHeaders(req);

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return Array.Empty<string>();

        var list = JsonSerializer.Deserialize<List<string>>(
            raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return list ;
    }

    private void AddDefaultHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    private sealed class EstimateDto
    {
        [JsonPropertyName("rate")]
        public decimal? Rate { get; set; }

        [JsonPropertyName("estimate")]
        public decimal? Estimate { get; set; }

        [JsonPropertyName("fee_from")]
        public decimal? FeeFrom { get; set; }

        [JsonPropertyName("fee_to")]
        public decimal? FeeTo { get; set; }
    }
}