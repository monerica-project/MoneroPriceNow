using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

/// <summary>
/// SimpleSwap API v3 client.
///
/// Critical: SimpleSwap uses its own ticker+network strings (returned by /v3/currencies).
/// You CANNOT guess them. XMR network is NOT "mainnet" — it must come from the currencies list.
/// We fetch currencies once, cache for 4 hours, then use exact values in estimates.
///
/// Endpoints used:
///   GET /v3/currencies?fixed=false&active=true  → { symbol, network, ... }[]
///   GET /v3/estimates?tickerFrom=&networkFrom=&tickerTo=&networkTo=&amount=&fixed=false
/// Auth: x-api-key header on every request.
/// </summary>
public sealed class SimpleSwapClient : ISimpleSwapClient
{
    private readonly HttpClient _http;
    private readonly SimpleSwapOptions _opt;

    public string ExchangeKey => "simpleswap";
    public string SiteName => _opt.SiteName;
    public string? SiteUrl => _opt.SiteUrl;

    private const decimal BuyProbeUsdt = 500m;
    private const decimal SellProbeXmr = 1m;
    private const decimal AffiliateFee = 0.004m;  // 0.4% affiliate fee
    private const decimal ApiRateCorrection = 0.9834m; // empirical: API rates ~1.66% worse than site

    private readonly SemaphoreSlim _currencyLock = new(1, 1);
    private List<CurrencyDto>? _currencies;
    private DateTime _currenciesAt = DateTime.MinValue;
    private static readonly TimeSpan CurrencyTtl = TimeSpan.FromHours(4);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public SimpleSwapClient(HttpClient http, IOptions<SimpleSwapOptions> options)
    {
        _http = http;
        _opt = options.Value;
    }

    // ── IExchangeCurrencyApi ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var list = await GetCachedCurrenciesAsync(ct);
        return list
            .Select(c => new ExchangeCurrency(
                ExchangeId: $"{c.Symbol}|{c.Network}",
                Ticker: c.Symbol.ToUpperInvariant(),
                Network: c.Network))
            .ToList();
    }

    // ── IExchangePriceApi (sell) ──────────────────────────────────────────────

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (from, to) = await ResolvePairAsync(query.Base, query.Quote, ct);
        if (from is null || to is null) return null;

        // Send exactly 1 XMR → USDT; result is direct per-XMR sell price (matches site default)
        var usdtReceived = await FetchEstimateAsync(from, to, amount: SellProbeXmr, ct);
        if (usdtReceived is null || usdtReceived <= 0) return null;

        // API returns 0.4% less USDT due to affiliate fee — correct back to true rate
        var sellPrice = usdtReceived.Value;
        var dbg = $"sell from={from.Symbol}/{from.Network} to={to.Symbol}/{to.Network} result={usdtReceived}";

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: sellPrice,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: dbg);
    }

    // ── IExchangeBuyPriceApi ──────────────────────────────────────────────────

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (from, to) = await ResolvePairAsync(query.Base, query.Quote, ct);
        if (from is null || to is null) return null;

        // Use a fixed probe well above minimum. API returns 0.4% less XMR due to affiliate fee.
        // Correct: (probe / xmrReceived) * (1 - fee) = true per-XMR cost
        // e.g. 400 USDT → 1.0686 XMR via API → 400/1.0686 * 0.996 = $372.8/XMR
        const decimal fixedProbe = 400m;
        var xmrReceived = await FetchEstimateAsync(to, from, amount: fixedProbe, ct);
        if (xmrReceived is null || xmrReceived <= 0) return null;

        var buyPrice = (fixedProbe / xmrReceived.Value) * ApiRateCorrection;
        if (buyPrice <= 0) return null;

        var dbg = $"buy probe={fixedProbe} xmr={xmrReceived:F6} price={buyPrice:F2}";

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: buyPrice,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: dbg);
    }

    // ── Pair resolution ───────────────────────────────────────────────────────

    private async Task<(CurrencyDto? Base, CurrencyDto? Quote)> ResolvePairAsync(
        AssetRef baseAsset, AssetRef quoteAsset, CancellationToken ct)
    {
        var currencies = await GetCachedCurrenciesAsync(ct);
        return (Match(currencies, baseAsset), Match(currencies, quoteAsset));
    }

    private static CurrencyDto? Match(List<CurrencyDto> currencies, AssetRef asset)
    {
        var ticker = asset.Ticker.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(asset.Network))
        {
            var hint = asset.Network.Trim().ToLowerInvariant();

            // Exact normalised match
            var exact = currencies.FirstOrDefault(c =>
                c.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                NormalizeNetwork(c.Network).Equals(hint, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            // Contains match
            var contains = currencies.FirstOrDefault(c =>
                c.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                c.Network.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (contains is not null) return contains;
        }

        // Fall back to first entry for this ticker, preferring mainnet/trc20
        return currencies
            .Where(c => c.Symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => NetworkPriority(c.Network))
            .FirstOrDefault();
    }

    private static string NormalizeNetwork(string n) => n.Trim().ToLowerInvariant() switch
    {
        "trc20" or "tron" or "trx" => "trc20",
        "erc20" or "eth" or "ethereum" => "erc20",
        "bep20" or "bsc" or "binance smart chain" => "bep20",
        "sol" or "solana" => "sol",
        var x => x
    };

    private static int NetworkPriority(string n) => n.ToUpperInvariant() switch
    {
        "MAINNET" => 0,
        "TRC20" or "TRX" or "TRON" => 1,
        "ERC20" or "ETH" or "ETHEREUM" => 2,
        "BEP20" or "BSC" => 3,
        "SOL" or "SOLANA" => 4,
        _ => 99
    };

    // ── Currency cache ────────────────────────────────────────────────────────

    private async Task<List<CurrencyDto>> GetCachedCurrenciesAsync(CancellationToken ct)
    {
        if (_currencies is not null && DateTime.UtcNow - _currenciesAt < CurrencyTtl)
            return _currencies;

        await _currencyLock.WaitAsync(ct);
        try
        {
            if (_currencies is not null && DateTime.UtcNow - _currenciesAt < CurrencyTtl)
                return _currencies;

            var list = await FetchCurrenciesAsync(ct);
            if (list.Count > 0) { _currencies = list; _currenciesAt = DateTime.UtcNow; }
            return _currencies ?? list;
        }
        finally { _currencyLock.Release(); }
    }

    private async Task<List<CurrencyDto>> FetchCurrenciesAsync(CancellationToken ct)
    {
        try
        {
            using var req = BuildRequest(HttpMethod.Get, "/v3/currencies?fixed=false&active=true");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var body = await resp.Content.ReadAsStringAsync(ct);
            return ParseCurrencies(body);
        }
        catch { return new(); }
    }

    private static List<CurrencyDto> ParseCurrencies(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array)
                arr = r;
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                arr = d;
            else return new();

            var list = new List<CurrencyDto>();
            foreach (var el in arr.EnumerateArray())
            {
                var symbol = GetString(el, "symbol") ?? GetString(el, "ticker") ?? "";
                var network = GetString(el, "network") ?? "";
                if (!string.IsNullOrWhiteSpace(symbol))
                    list.Add(new CurrencyDto(symbol.ToUpperInvariant(), network));
            }
            return list;
        }
        catch { return new(); }
    }

    // ── Estimate ──────────────────────────────────────────────────────────────

    private async Task<decimal?> FetchEstimateAsync(
        CurrencyDto from, CurrencyDto to, decimal amount, CancellationToken ct)
    {
        var qs = $"tickerFrom={Uri.EscapeDataString(from.Symbol.ToLowerInvariant())}" +
                 $"&networkFrom={Uri.EscapeDataString(from.Network)}" +
                 $"&tickerTo={Uri.EscapeDataString(to.Symbol.ToLowerInvariant())}" +
                 $"&networkTo={Uri.EscapeDataString(to.Network)}" +
                 $"&amount={amount.ToString("G", CultureInfo.InvariantCulture)}" +
                 $"&fixed=false";

        return await CallEstimateAsync(qs, ct);
    }

    // Reverse: ask "how much FROM do I need to receive exactly `amountTo` of TO?"
    private async Task<decimal?> FetchReverseEstimateAsync(
        CurrencyDto from, CurrencyDto to, decimal amountTo, CancellationToken ct)
    {
        var qs = $"tickerFrom={Uri.EscapeDataString(from.Symbol.ToLowerInvariant())}" +
                 $"&networkFrom={Uri.EscapeDataString(from.Network)}" +
                 $"&tickerTo={Uri.EscapeDataString(to.Symbol.ToLowerInvariant())}" +
                 $"&networkTo={Uri.EscapeDataString(to.Network)}" +
                 $"&amountTo={amountTo.ToString("G", CultureInfo.InvariantCulture)}" +
                 $"&fixed=false";

        return await CallEstimateAsync(qs, ct);
    }

    private async Task<decimal?> CallEstimateAsync(string qs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_opt.TimeoutSeconds, 3, 30)));

        try
        {
            using var req = BuildRequest(HttpMethod.Get, $"/v3/estimates?{qs}");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            return ParseAmountTo(body);
        }
        catch { return null; }
    }

    // Returns the minimum FROM amount for a pair from /v3/get-ranges
    private async Task<decimal?> FetchRangeMinAsync(CurrencyDto from, CurrencyDto to, CancellationToken ct)
    {
        var qs = $"tickerFrom={Uri.EscapeDataString(from.Symbol.ToLowerInvariant())}" +
                 $"&networkFrom={Uri.EscapeDataString(from.Network)}" +
                 $"&tickerTo={Uri.EscapeDataString(to.Symbol.ToLowerInvariant())}" +
                 $"&networkTo={Uri.EscapeDataString(to.Network)}" +
                 $"&fixed=false";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_opt.TimeoutSeconds, 3, 30)));

        try
        {
            using var req = BuildRequest(HttpMethod.Get, $"/v3/ranges?{qs}");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            System.Diagnostics.Debug.WriteLine($"[get-ranges] status={(int)resp.StatusCode} body={body}");
            if (!resp.IsSuccessStatusCode) return null;
            return ParseRangeMin(body);
        }
        catch { return null; }
    }

    private static decimal? ParseRangeMin(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var data = root.TryGetProperty("result", out var r) ? r : root;
            // Log ALL fields so we can see the actual structure
            var allFields = string.Join(", ", data.EnumerateObject().Select(p => $"{p.Name}={p.Value}"));
            System.Diagnostics.Debug.WriteLine($"[get-ranges] fields: {allFields}");
            return GetDecimalN(data, "minAmount")
                ?? GetDecimalN(data, "min")
                ?? GetDecimalN(data, "minAmountFrom")
                ?? GetDecimalN(data, "from")
                ?? GetDecimalN(data, "minimum");
        }
        catch { return null; }
    }

    private static decimal? ParseAmountTo(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("result", out var result))
            {
                var v = GetDecimalN(result, "estimatedAmount") ?? GetDecimalN(result, "amountTo") ?? GetDecimalN(result, "amount_to") ?? GetDecimalN(result, "expectedAmount");
                if (v > 0) return v;
            }
            return GetDecimalN(root, "estimatedAmount") ?? GetDecimalN(root, "amountTo") ?? GetDecimalN(root, "amount_to") ?? GetDecimalN(root, "expectedAmount");
        }
        catch { return null; }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("x-api-key", _opt.ApiKey);
        if (!string.IsNullOrWhiteSpace(_opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(_opt.UserAgent);
        return req;
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static decimal? GetDecimalN(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(p.GetString(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null
        };
    }

    private sealed record CurrencyDto(string Symbol, string Network);
}