using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class OctoSwapClient : IOctoSwapClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int DefaultTimeoutSeconds = 8;

    private readonly HttpClient http;
    private readonly OctoSwapOptions opt;

    public string ExchangeKey => "octoswap";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    public OctoSwapClient(HttpClient http, IOptions<OctoSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // SELL: 1 XMR -> how much USDT(TRX) you receive
    // amount=1 XMR is always above minimum, dto.Price = USDT received for 1 XMR
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        var fromSymbol = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToUpperInvariant();
        var toSymbol = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToUpperInvariant();
        var fromNetwork = ToOctoNetwork(fromSymbol, query.Base.Network);
        var toNetwork = ToOctoNetwork(toSymbol, query.Quote.Network);

        var url = BuildUrl(fromSymbol, toSymbol, fromNetwork, toNetwork, amount: 1m);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);

        var res = await SafeHttpExtensions.SendForStringAsync(http, req, TimeSpan.FromSeconds(DefaultTimeoutSeconds), ct);
        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        PriceDto? dto;
        try { dto = JsonSerializer.Deserialize<PriceDto>(res.Body, JsonOpt); }
        catch { return null; }

        if (dto is null || dto.Price <= 0) return null;

        return Result(query, (decimal)dto.Price);
    }

    // BUY: how much USDT(TRX) is needed to get 1 XMR
    // Probe with a realistic USDT amount; dto.Price = total XMR out for that amount.
    // buyPrice (USDT per 1 XMR) = probeAmount / dto.Price
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        var usdtSymbol = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToUpperInvariant();
        var xmrSymbol = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToUpperInvariant();
        var usdtNetwork = ToOctoNetwork(usdtSymbol, query.Quote.Network);
        var xmrNetwork = ToOctoNetwork(xmrSymbol, query.Base.Network);

        // First: probe with configured amount to discover min/max limits
        var probe = opt.BuyProbeAmountUsdt;
        var url = BuildUrl(usdtSymbol, xmrSymbol, usdtNetwork, xmrNetwork, amount: probe);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);

        var res = await SafeHttpExtensions.SendForStringAsync(http, req, TimeSpan.FromSeconds(DefaultTimeoutSeconds), ct);
        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        PriceDto? dto;
        try { dto = JsonSerializer.Deserialize<PriceDto>(res.Body, JsonOpt); }
        catch { return null; }

        if (dto is null || dto.Price <= 0) return null;

        // Clamp probe within min/max if the API told us the limits
        var minDep = (decimal)dto.MinDeposit;
        var maxDep = (decimal)dto.MaxDeposit;
        var clamped = probe;
        if (minDep > 0 && clamped < minDep) clamped = minDep * 1.1m;
        if (maxDep > 0 && clamped > maxDep) clamped = maxDep * 0.9m;

        // If clamping changed the amount materially, re-fetch with corrected amount
        if (Math.Abs(clamped - probe) / probe > 0.01m)
        {
            using var req2 = new HttpRequestMessage(HttpMethod.Get,
                BuildUrl(usdtSymbol, xmrSymbol, usdtNetwork, xmrNetwork, amount: clamped));
            AddAuth(req2);
            var res2 = await SafeHttpExtensions.SendForStringAsync(http, req2, TimeSpan.FromSeconds(DefaultTimeoutSeconds), ct);
            if (res2 is null || (int)res2.Status < 200 || (int)res2.Status >= 300) return null;
            try { dto = JsonSerializer.Deserialize<PriceDto>(res2.Body, JsonOpt); }
            catch { return null; }
            if (dto is null || dto.Price <= 0) return null;
            probe = clamped;
        }

        // dto.Price = total XMR received for `probe` USDT
        var buyPrice = probe / (decimal)dto.Price;
        if (buyPrice <= 0) return null;

        return Result(query, buyPrice);
    }

    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Helpers ──────────────────────────────────────────────────────

    private static string BuildUrl(
        string from, string to,
        string? fromNetwork, string? toNetwork,
        decimal amount)
    {
        var url = $"/api/price" +
                  $"?amount={amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&type=variable" +
                  $"&from={Uri.EscapeDataString(from)}" +
                  $"&to={Uri.EscapeDataString(to)}";

        if (!string.IsNullOrWhiteSpace(fromNetwork))
            url += $"&from_network={Uri.EscapeDataString(fromNetwork)}";

        if (!string.IsNullOrWhiteSpace(toNetwork))
            url += $"&to_network={Uri.EscapeDataString(toNetwork)}";

        return url;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("api-key", opt.ApiKey);

        if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);

    private static string? ToOctoNetwork(string ticker, string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return null;

        return network.Trim() switch
        {
            "Mainnet" => null,
            "Tron" => "TRX",
            "Ethereum" => "ETH",
            "Binance Smart Chain" => "BSC",
            "Solana" => "SOL",
            _ => network.Trim()
        };
    }

    private sealed class PriceDto
    {
        [JsonPropertyName("price")]
        public double Price { get; set; }

        [JsonPropertyName("min_deposit")]
        public double MinDeposit { get; set; }

        [JsonPropertyName("max_deposit")]
        public double MaxDeposit { get; set; }

        [JsonPropertyName("withdrawal_fee")]
        public double WithdrawalFee { get; set; }
    }
}