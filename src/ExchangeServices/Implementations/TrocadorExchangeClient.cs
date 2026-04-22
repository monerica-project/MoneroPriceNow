using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Trocador aggregator API client.
///
/// Endpoints used:
///   GET /new_rate    → floating rate for a pair across all integrated providers
///   GET /coins       → full list of supported coins with ticker + network + minimum
///   GET /coin        → single coin lookup (used here to fetch minimum amounts)
///
/// Auth: API key passed as the 'API-Key' request header.
///
/// Rate semantics:
///   new_rate accepts amount_from and returns amount_to = units of ticker_to received.
///
///   Sell (XMR → USDT): ticker_from=xmr, amount_from=1              → amount_to = USDT per 1 XMR (direct)
///   Buy  (USDT → XMR): ticker_from=usdt, amount_from=BuyRef        → amount_to = XMR for BuyRef USDT
///                       buyPrice = BuyReferenceAmountUsdt / amount_to
///
/// MinAmountUsd:
///   Sell: minimum XMR (from /coin?ticker=xmr) × sell price (AmountTo) = USD minimum
///   Buy:  minimum USDT (from /coin?ticker=usdt) = USD minimum directly
/// </summary>
public sealed class TrocadorClient : ITrocadorClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly TrocadorOptions opt;

    public string ExchangeKey => "trocador";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;

    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public TrocadorClient(HttpClient http, IOptions<TrocadorOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (tickerFrom, networkFrom) = ToTicker(query.Base);
        var (tickerTo, networkTo) = ToTicker(query.Quote);

        if (tickerFrom is null || tickerTo is null) return null;

        var rateTask = GetRateAsync(tickerFrom, networkFrom, tickerTo, networkTo, amountFrom: 1m, ct);
        var minTask = GetCoinMinimumAsync("usdt", opt.UsdtNetwork, ct); // already USD

        await Task.WhenAll(rateTask, minTask);

        var dto = await rateTask;
        if (dto is null || dto.AmountTo <= 0) return null;

        var minUsdt = await minTask;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.AmountTo,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"sell ticker_from={tickerFrom} ticker_to={tickerTo} amount_to={dto.AmountTo} provider={dto.Provider}",
            MinAmountUsd: minUsdt > 0m ? minUsdt : (opt.MinAmountUsd > 0m ? opt.MinAmountUsd : null)
        );
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (tickerFrom, networkFrom) = ToTicker(query.Quote);
        var (tickerTo, networkTo) = ToTicker(query.Base);

        if (tickerFrom is null || tickerTo is null) return null;

        var rateTask = GetRateAsync(tickerFrom, networkFrom, tickerTo, networkTo, opt.BuyReferenceAmountUsdt, ct);
        var minTask = GetCoinMinimumAsync("usdt", opt.UsdtNetwork, ct); // already USD

        await Task.WhenAll(rateTask, minTask);

        var dto = await rateTask;
        if (dto is null || dto.AmountTo <= 0) return null;

        var buyPrice = opt.BuyReferenceAmountUsdt / dto.AmountTo;
        var minUsdt = await minTask;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: buyPrice,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"buy ticker_from={tickerFrom} ticker_to={tickerTo} ref={opt.BuyReferenceAmountUsdt} amount_to={dto.AmountTo} buyPrice={buyPrice:F6} provider={dto.Provider}",
            MinAmountUsd: minUsdt > 0m ? minUsdt : (opt.MinAmountUsd > 0m ? opt.MinAmountUsd : null)
        );
    }

    // =========================
    // CURRENCIES
    // GET /coins
    // Returns array of { name, ticker, network, minimum, maximum }
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/coins");
        AddApiKey(req);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);
        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
            return Array.Empty<ExchangeCurrency>();

        var dto = JsonSerializer.Deserialize<List<TrocadorCoinItem>>(res.Body, JsonOpt);
        if (dto is null) return Array.Empty<ExchangeCurrency>();

        return dto
            .Where(c => !string.IsNullOrWhiteSpace(c.Ticker))
            .Select(c => new ExchangeCurrency(
                ExchangeId: $"{c.Ticker!.ToLowerInvariant()}_{c.Network?.ToLowerInvariant()}",
                Ticker: c.Ticker!.ToUpperInvariant(),
                Network: c.Network ?? c.Ticker
            ))
            .OrderBy(x => x.Ticker)
            .ToList();
    }

    // =========================
    // PRIVATE: GET /coin?ticker=xxx
    // Returns the minimum trade amount for the given ticker+network, or null.
    // =========================
    private async Task<decimal?> GetCoinMinimumAsync(string ticker, string network, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"/coin?ticker={Uri.EscapeDataString(ticker)}");
            AddApiKey(req);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);
            if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

            var coins = JsonSerializer.Deserialize<List<TrocadorCoinItem>>(res.Body, JsonOpt);
            if (coins is null || coins.Count == 0) return null;

            // Match by network if provided; fall back to first result
            var match = coins.FirstOrDefault(c =>
                string.Equals(c.Network, network, StringComparison.OrdinalIgnoreCase))
                ?? coins[0];

            return match.Minimum > 0m ? match.Minimum : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TROCADOR MIN] Error fetching minimum for {ticker}/{network}: {ex.Message}");
            return null;
        }
    }

    // =========================
    // PRIVATE: GET /new_rate
    // =========================
    private async Task<TrocadorRateResult?> GetRateAsync(
        string tickerFrom, string networkFrom,
        string tickerTo, string networkTo,
        decimal amountFrom,
        CancellationToken ct)
    {
        var qs = $"ticker_from={Uri.EscapeDataString(tickerFrom)}" +
                 $"&network_from={Uri.EscapeDataString(networkFrom)}" +
                 $"&ticker_to={Uri.EscapeDataString(tickerTo)}" +
                 $"&network_to={Uri.EscapeDataString(networkTo)}" +
                 $"&amount_from={amountFrom.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}" +
                 $"&payment=False";

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/new_rate?{qs}");
        AddApiKey(req);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[TROCADOR RATE] status={res?.Status} body={res?.Body?[..Math.Min(200, res?.Body?.Length ?? 0)]}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        return JsonSerializer.Deserialize<TrocadorRateResult>(res.Body, JsonOpt);
    }

    // =========================
    // HELPERS
    // =========================

    /// <summary>Maps an AssetRef to Trocador's (ticker, network) pair.</summary>
    private (string? Ticker, string Network) ToTicker(AssetRef asset)
    {
        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        var net = (asset.Network ?? "").Trim();

        return ticker switch
        {
            "XMR" => ("xmr", "Mainnet"),
            "BTC" => ("btc", "Mainnet"),
            "ETH" => ("eth", "Mainnet"),
            "LTC" => ("ltc", "Mainnet"),
            "BNB" => ("bnb", "BSC"),
            "DOGE" => ("doge", "Mainnet"),
            "SOL" => ("sol", "Mainnet"),
            "XRP" => ("xrp", "Mainnet"),
            "ADA" => ("ada", "Mainnet"),
            "DOT" => ("dot", "Mainnet"),
            "TRX" => ("trx", "Mainnet"),
            "BCH" => ("bch", "Mainnet"),
            "USDT" => net switch
            {
                "Tron" => ("usdt", "TRC20"),
                "Ethereum" => ("usdt", "ERC20"),
                "Binance Smart Chain" => ("usdt", "BEP20"),
                _ => ("usdt", opt.UsdtNetwork),
            },
            "USDC" => ("usdc", "ERC20"),
            "DAI" => ("dai", "ERC20"),
            "LINK" => ("link", "ERC20"),
            "XLM" => ("xlm", "Mainnet"),
            "ATOM" => ("atom", "Mainnet"),
            "DASH" => ("dash", "Mainnet"),
            "ZEC" => ("zec", "Mainnet"),
            _ => (null, string.Empty),
        };
    }

    private void AddApiKey(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
            req.Headers.TryAddWithoutValidation("API-Key", opt.ApiKey);
    }

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

    // =========================
    // DTOs
    // =========================
    private sealed class TrocadorRateResult
    {
        [JsonPropertyName("trade_id")] public string? TradeId { get; set; }
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("amount_from")] public decimal AmountFrom { get; set; }
        [JsonPropertyName("amount_to")] public decimal AmountTo { get; set; }
        [JsonPropertyName("fixed")] public bool Fixed { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class TrocadorCoinItem
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("ticker")] public string? Ticker { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("minimum")] public decimal Minimum { get; set; }
        [JsonPropertyName("maximum")] public decimal Maximum { get; set; }
    }
}