using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class NanswapClient : INanswapClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient http;
    private readonly NanswapOptions opt;

    public string  ExchangeKey => "nanswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    public NanswapClient(HttpClient http, IOptions<NanswapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> ? USDT
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var fromKeyCandidates = GetKeyCandidates(query.Base);
        var toKeyCandidates = GetKeyCandidates(query.Quote);

        var fromKeys = PreferExchangeIdFirst(fromKeyCandidates, query.Base.ExchangeId);
        var toKeys = PreferExchangeIdFirst(toKeyCandidates, query.Quote.ExchangeId);

        foreach (var fromKey in fromKeys)
            foreach (var toKey in toKeys)
            {
                var dto = await GetEstimateAsync(fromKey, toKey, amount: 1m, ct);
                if (dto is null || dto.AmountTo <= 0) continue;

                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: dto.AmountTo,                 // USDT per 1 XMR
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }

        return null;
    }

    // ==========================================
    // BUY: ? USDT needed to receive ~1 XMR
    // Nanswap reverse estimate is feeless-only,
    // so we must do forward USDT->XMR and invert.
    // ==========================================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var usdtAsset = query.Quote; // USDTTRC
        var xmrAsset = query.Base;   // XMR

        var fromKeyCandidates = GetKeyCandidates(usdtAsset);
        var toKeyCandidates = GetKeyCandidates(xmrAsset);

        var fromKeys = PreferExchangeIdFirst(fromKeyCandidates, usdtAsset.ExchangeId);
        var toKeys = PreferExchangeIdFirst(toKeyCandidates, xmrAsset.ExchangeId);

        foreach (var fromKey in fromKeys)
            foreach (var toKey in toKeys)
            {
                // ✅ Step 1: ask Nanswap what amounts are valid for this pair
                var limits = await GetLimitsAsync(fromKey, toKey, ct);
                if (limits is null) continue;

                var min = limits.Min <= 0 ? 0.0001m : limits.Min;
                var max = limits.Max.HasValue && limits.Max.Value > 0 ? limits.Max.Value : (decimal?)null;

                // ✅ Step 2: pick a starting amount inside [min, max]
                // - Start with something that usually clears mins for non-feeless pairs
                // - Clamp to pair max if present
                var startUsdt = 200m;
                startUsdt = Math.Max(startUsdt, min);

                if (max.HasValue)
                    startUsdt = Math.Min(startUsdt, max.Value);

                // If max < min (bad pair data), skip
                if (max.HasValue && max.Value < min)
                    continue;

                // ✅ Step 3: forward estimate USDT -> XMR
                var first = await GetEstimateAsync(fromKey, toKey, startUsdt, ct);
                if (first is null || first.AmountTo <= 0) continue;

                // USDT per XMR
                var p1 = SafeDiv(startUsdt, first.AmountTo);
                if (p1 <= 0) continue;

                // ✅ Step 4: refine once using implied amount for ~1 XMR
                // Use p1 as an estimate for "USDT needed for 1 XMR"
                var refineUsdt = p1;

                // clamp to limits
                refineUsdt = Math.Max(refineUsdt, min);
                if (max.HasValue)
                    refineUsdt = Math.Min(refineUsdt, max.Value);

                // If clamped refine == start, no need second call (but we can still do it)
                var second = await GetEstimateAsync(fromKey, toKey, refineUsdt, ct);
                if (second is null || second.AmountTo <= 0) continue;

                var p2 = SafeDiv(refineUsdt, second.AmountTo);
                if (p2 <= 0) continue;

                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: p2,                           // USDT needed per 1 XMR (approx)
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }

        return null;
    }

    // =========================
    // CURRENCIES
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/all-currencies");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return Array.Empty<ExchangeCurrency>();

            // Response: { "USDT-TRX": { ticker:"usdt", network:"trx", ... }, "XMR": {...}, ... }
            var map = JsonSerializer.Deserialize<Dictionary<string, CurrencyDto>>(raw, JsonOpt);
            if (map is null || map.Count == 0)
                return Array.Empty<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>(map.Count);

            foreach (var kvp in map)
            {
                var key = kvp.Key?.Trim();
                if (string.IsNullOrWhiteSpace(key)) continue;

                var dto = kvp.Value;
                var ticker = (dto.Ticker ?? key).Trim().ToUpperInvariant();
                var network = NormalizeNetwork(dto.Network);

                list.Add(new ExchangeCurrency(
                    ExchangeId: key,      // exact key used by Nanswap in from/to params
                    Ticker: ticker,
                    Network: network
                ));
            }

            return list
                .OrderBy(x => x.Ticker)
                .ThenBy(x => x.Network)
                .ToList();
        }
        catch (HttpRequestException)
        {
            return Array.Empty<ExchangeCurrency>();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<ExchangeCurrency>();
        }
    }

    // =========================
    // INTERNAL HTTP
    // =========================
    private async Task<EstimateDto?> GetEstimateAsync(string fromKey, string toKey, decimal amount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromKey) || string.IsNullOrWhiteSpace(toKey) || amount <= 0)
            return null;

        var qs =
            $"from={Uri.EscapeDataString(fromKey)}&" +
            $"to={Uri.EscapeDataString(toKey)}&" +
            $"amount={Uri.EscapeDataString(amount.ToString(CultureInfo.InvariantCulture))}";

        var url = $"/v1/get-estimate?{qs}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<EstimateDto>(raw, JsonOpt);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<LimitsDto?> GetLimitsAsync(string fromKey, string toKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromKey) || string.IsNullOrWhiteSpace(toKey))
            return null;

        var qs =
            $"from={Uri.EscapeDataString(fromKey)}&" +
            $"to={Uri.EscapeDataString(toKey)}";

        var url = $"/v1/get-limits?{qs}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return null;

            var dto = JsonSerializer.Deserialize<LimitsDto>(raw, JsonOpt);
            if (dto is null || dto.Min <= 0) return null;

            return dto;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // =========================
    // KEY HELPERS
    // =========================
    private static IEnumerable<string> PreferExchangeIdFirst(IEnumerable<string> candidates, string? exchangeId)
    {
        var list = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(exchangeId))
        {
            list.RemoveAll(x => x.Equals(exchangeId, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, exchangeId);
        }

        return list;
    }

    private static IEnumerable<string> GetKeyCandidates(AssetRef asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId))
            yield return asset.ExchangeId;

        if (!string.IsNullOrWhiteSpace(asset.Ticker))
            yield return asset.Ticker.Trim().ToUpperInvariant();

        // USDT Tron common keys on Nanswap
        if (asset.Ticker.Equals("USDT", StringComparison.OrdinalIgnoreCase) &&
            (asset.Network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            yield return "USDT-TRX";     // most common
            yield return "USDT-TRC20";
            yield return "USDTTRC20";
            yield return "USDT-TRON";
        }
    }

    private static string NormalizeNetwork(string? network)
    {
        if (string.IsNullOrWhiteSpace(network))
            return "Mainnet";

        var n = network.Trim().ToLowerInvariant();

        return n switch
        {
            "trx" => "Tron",
            "tron" => "Tron",
            "trc20" => "Tron",

            "eth" => "Ethereum",
            "erc20" => "Ethereum",
            "ethereum" => "Ethereum",

            "bsc" => "Binance Smart Chain",
            "binance-smart-chain" => "Binance Smart Chain",

            "sol" => "Solana",
            "solana" => "Solana",

            "arbitrum" => "Arbitrum",
            "base" => "Base",
            "matic" => "Polygon",
            "polygon" => "Polygon",

            "btc" => "Mainnet",
            "xmr" => "Mainnet",
            "ltc" => "Mainnet",
            "doge" => "Mainnet",

            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n)
        };
    }

    private static decimal SafeDiv(decimal a, decimal b) => b == 0 ? 0 : a / b;

    private sealed class CurrencyDto
    {
        [JsonPropertyName("ticker")]
        public string? Ticker { get; set; }

        [JsonPropertyName("network")]
        public string? Network { get; set; }

        [JsonPropertyName("feeless")]
        public bool? Feeless { get; set; }
    }

    private sealed class EstimateDto
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }

        [JsonPropertyName("amountFrom")]
        public decimal AmountFrom { get; set; }

        [JsonPropertyName("amountTo")]
        public decimal AmountTo { get; set; }
    }

    private sealed class LimitsDto
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }

        [JsonPropertyName("min")]
        public decimal Min { get; set; }

        [JsonPropertyName("max")]
        public decimal? Max { get; set; }
    }
}