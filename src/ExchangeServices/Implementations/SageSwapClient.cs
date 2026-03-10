using ExchangeServices.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ExchangeServices.Http;

namespace ExchangeServices.SageSwap;

public sealed class SageSwapClient : ISageSwapClient, IExchangeCurrencyApi
{
    // All amounts in the SageSwap API are in "gwei" (1 coin = 1_000_000_000)
    private const long OneCoinGwei = 1_000_000_000;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient http;
    private readonly SageSwapOptions opt;

    public string  ExchangeKey => "sageswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    public SageSwapClient(HttpClient http, IOptions<SageSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/currencies");
        AddHeaders(req);

        var res = await http.SendForStringWithTimeoutAsync(req, DefaultTimeout, ct);
        if (res is null) return Array.Empty<ExchangeCurrency>();
        if (res.StatusCode < HttpStatusCode.OK || res.StatusCode >= HttpStatusCode.MultipleChoices)
            return Array.Empty<ExchangeCurrency>();

        CurrenciesResponse? dto;
        try { dto = JsonSerializer.Deserialize<CurrenciesResponse>(res.Body, JsonOpt); }
        catch { return Array.Empty<ExchangeCurrency>(); }

        if (dto?.Data is null || dto.Data.Count == 0)
            return Array.Empty<ExchangeCurrency>();

        return dto.Data
            .Select(x => new ExchangeCurrency(
                ExchangeId: x.FriendlyId,
                Ticker: x.Ticker,
                Network: x.Network?.Name ?? ""
            ))
            .ToList();
    }

    // SELL: send 1 BASE → receive X QUOTE
    // Uses input_currency_amount = 1 coin (gwei), reads outputCurrencyAmount
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var baseId = await ResolveFriendlyIdAsync(query.Base, ct);
        var quoteId = await ResolveFriendlyIdAsync(query.Quote, ct);

        if (string.IsNullOrWhiteSpace(baseId) || string.IsNullOrWhiteSpace(quoteId))
            return null;

        // Ask: if I send 1 XMR (1_000_000_000 gwei), how much USDT do I get?
        var url =
            $"api/v1/rate" +
            $"?input_currency={Uri.EscapeDataString(baseId)}" +
            $"&output_currency={Uri.EscapeDataString(quoteId)}" +
            $"&input_currency_amount={OneCoinGwei}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req);

        var res = await http.SendForStringWithTimeoutAsync(req, DefaultTimeout, ct);
        if (res is null) return null;
        if (res.StatusCode < HttpStatusCode.OK || res.StatusCode >= HttpStatusCode.MultipleChoices)
            return null;

        RateResponse? rate;
        try { rate = JsonSerializer.Deserialize<RateResponse>(res.Body, JsonOpt); }
        catch { return null; }

        var outAmtGwei = rate?.Data?.OutputCurrencyAmount;
        if (outAmtGwei is null || outAmtGwei <= 0) return null;

        // outputCurrencyAmount is USDT in gwei → divide by 1e9 to get USDT coins
        var quotePerOneBase = outAmtGwei.Value / (decimal)OneCoinGwei;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: quotePerOneBase, // USDT received per 1 XMR sold
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: res.CorrelationId,
            Raw: null);
    }

    // BUY: how much QUOTE needed to receive 1 BASE
    // output_currency_amount is unreliable — API returns inputCurrencyAmount for >1 XMR.
    // Instead: probe with a real USDT input, then compute rate from BOTH returned values.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var baseId = await ResolveFriendlyIdAsync(query.Base, ct);
        var quoteId = await ResolveFriendlyIdAsync(query.Quote, ct);

        if (string.IsNullOrWhiteSpace(baseId) || string.IsNullOrWhiteSpace(quoteId))
            return null;

        // Probe with 500 USDT — large enough to clear minimum trade size.
        // We divide inputCurrencyAmount / outputCurrencyAmount to get USDT-per-XMR
        // regardless of how many XMR the probe actually buys.
        const long probeGwei = 500L * OneCoinGwei; // 500 USDT in gwei

        var url =
            $"api/v1/rate" +
            $"?input_currency={Uri.EscapeDataString(quoteId)}" +  // USDT
            $"&output_currency={Uri.EscapeDataString(baseId)}" +  // XMR
            $"&input_currency_amount={probeGwei}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req);

        var res = await http.SendForStringWithTimeoutAsync(req, DefaultTimeout, ct);
        if (res is null) return null;
        if (res.StatusCode < HttpStatusCode.OK || res.StatusCode >= HttpStatusCode.MultipleChoices)
            return null;

        RateResponse? rate;
        try { rate = JsonSerializer.Deserialize<RateResponse>(res.Body, JsonOpt); }
        catch { return null; }

        var inGwei = rate?.Data?.InputCurrencyAmount;
        var outGwei = rate?.Data?.OutputCurrencyAmount;

        if (inGwei is null || inGwei <= 0) return null;
        if (outGwei is null || outGwei <= 0) return null;

        // Both values in gwei — ratio gives USDT per XMR directly:
        // e.g. 500_000_000_000 USDT gwei / 1_447_000_000 XMR gwei = ~345.5 USDT per XMR
        var usdtPerXmr = (decimal)inGwei.Value / (decimal)outGwei.Value;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: usdtPerXmr,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: res.CorrelationId,
            Raw: null);
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(opt.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.Token);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<string?> ResolveFriendlyIdAsync(AssetRef asset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId))
            return asset.ExchangeId;

        var currencies = await GetCurrenciesAsync(ct);
        if (currencies.Count == 0) return null;

        var ticker = asset.Ticker.Trim();
        var net = (asset.Network ?? "").Trim();

        if (string.IsNullOrWhiteSpace(net))
        {
            return currencies.FirstOrDefault(c =>
                c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)
            )?.ExchangeId;
        }

        return currencies.FirstOrDefault(c =>
            c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            c.Network.Equals(net, StringComparison.OrdinalIgnoreCase)
        )?.ExchangeId;
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    private sealed class CurrenciesResponse
    {
        public List<CurrencyDto> Data { get; set; } = new();
    }

    private sealed class CurrencyDto
    {
        public string FriendlyId { get; set; } = "";
        public string Ticker { get; set; } = "";
        public NetworkDto? Network { get; set; }
    }

    private sealed class NetworkDto
    {
        public string Name { get; set; } = "";
    }

    private sealed class RateResponse
    {
        public RateData? Data { get; set; }
    }

    private sealed class RateData
    {
        // USDT gwei needed to send (used in buy: output_currency_amount query)
        public long? InputCurrencyAmount { get; set; }

        // USDT gwei received (used in sell: input_currency_amount query)
        public long? OutputCurrencyAmount { get; set; }
    }
}