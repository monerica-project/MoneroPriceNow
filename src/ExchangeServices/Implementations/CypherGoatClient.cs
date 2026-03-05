using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class CypherGoatClient : ICypherGoatClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient http;
    private readonly CypherGoatOptions opt;

    public string  ExchangeKey => "cyphergoat";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;

    public CypherGoatClient(HttpClient http, IOptions<CypherGoatOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ==========================================
    // SELL: 1 XMR -> ? USDT(TRX)
    // Return HIGHEST amount from results[]
    // ==========================================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var coin1 = ToCgCoin(query.Base);
        var coin2 = ToCgCoin(query.Quote);
        var net1 = ToCgNetwork(query.Base);
        var net2 = ToCgNetwork(query.Quote);

        if (string.IsNullOrWhiteSpace(coin1) || string.IsNullOrWhiteSpace(coin2) ||
            string.IsNullOrWhiteSpace(net1) || string.IsNullOrWhiteSpace(net2))
            return null;

        // start at 1 base
        var amountIn = 1m;

        var est = await GetEstimateWithRetryAsync(coin1, coin2, net1, net2, amountIn, ct);
        if (est is null) return null;

        // If their min is > 1, bump amount and later convert to per-1
        if (est.Min > 0 && amountIn < est.Min)
        {
            amountIn = est.Min * 1.05m;
            est = await GetEstimateWithRetryAsync(coin1, coin2, net1, net2, amountIn, ct);
            if (est is null) return null;
        }

        if (est.Results is null || est.Results.Count == 0)
            return null;

        // Highest USDT received
        var bestOut = est.Results.Max(r => r.Amount);
        if (bestOut <= 0) return null;

        // Convert to price per 1 base
        var sellPer1 = bestOut / amountIn;
        if (sellPer1 <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: sellPer1,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // ==========================================
    // BUY: ? USDT needed to receive 1 XMR
    // Return LOWEST buy price (USDT per XMR)
    // computed from results[] as: (amountIn / amountOutXmr)
    // ==========================================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var usdt = query.Quote;
        var xmr = query.Base;

        var coin1 = ToCgCoin(usdt);   // usdt
        var coin2 = ToCgCoin(xmr);    // xmr
        var net1 = ToCgNetwork(usdt); // trx
        var net2 = ToCgNetwork(xmr);  // xmr

        if (string.IsNullOrWhiteSpace(coin1) || string.IsNullOrWhiteSpace(coin2) ||
            string.IsNullOrWhiteSpace(net1) || string.IsNullOrWhiteSpace(net2))
            return null;

        // Probe once to learn min, then choose a safe amount
        var probe = await GetEstimateWithRetryAsync(coin1, coin2, net1, net2, 1m, ct);
        if (probe is null) return null;

        var min = probe.Min;
        var amountIn = 100m;
        if (min > 0) amountIn = Math.Max(amountIn, min * 1.25m);

        var first = await GetEstimateWithRetryAsync(coin1, coin2, net1, net2, amountIn, ct);
        if (first?.Results is null || first.Results.Count == 0) return null;

        // Compute USDT per XMR for each provider and take the LOWEST
        var bestP1 = LowestUsdtPerXmr(first.Results, amountIn);
        if (bestP1 <= 0) return null;

        // Refine once near ~1 XMR target
        var refineIn = bestP1; // approx USDT to get 1 XMR
        if (min > 0 && refineIn < min) refineIn = min * 1.25m;
        refineIn = Clamp(refineIn, 10m, 100000m);

        var second = await GetEstimateWithRetryAsync(coin1, coin2, net1, net2, refineIn, ct);
        if (second?.Results is null || second.Results.Count == 0)
        {
            // fallback to p1
            return new PriceResult(ExchangeKey, query.Base, query.Quote, bestP1, DateTimeOffset.UtcNow, null, null);
        }

        var bestP2 = LowestUsdtPerXmr(second.Results, refineIn);
        if (bestP2 <= 0)
            bestP2 = bestP1;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: bestP2, // LOWEST buy price
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // =========================
    // Internal: /estimate
    // =========================
    private async Task<EstimateResponse?> GetEstimateWithRetryAsync(
        string coin1, string coin2,
        string network1, string network2,
        decimal amount,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        var attempts = Math.Clamp(opt.RetryCount, 1, 6);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var qs =
                $"coin1={Uri.EscapeDataString(coin1)}&" +
                $"coin2={Uri.EscapeDataString(coin2)}&" +
                $"amount={Uri.EscapeDataString(amount.ToString(CultureInfo.InvariantCulture))}&" +
                $"network1={Uri.EscapeDataString(network1)}&" +
                $"network2={Uri.EscapeDataString(network2)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, $"/estimate?{qs}");
            AddAuth(req);

            Http.SafeHttpExtensions.Result? res = null;
            try
            {
                res = await Http.SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                res = null;
            }

            if (res is null)
            {
                if (attempt < attempts) { await Backoff(attempt, ct); continue; }
                return null;
            }

            if (res.Status >= HttpStatusCode.OK && res.Status < HttpStatusCode.MultipleChoices)
            {
                try
                {
                    var dto = JsonSerializer.Deserialize<EstimateResponse>(res.Body, JsonOpt);
                    return dto;
                }
                catch
                {
                    return null;
                }
            }

            if (IsTransient(res.Status) && attempt < attempts)
            {
                await Backoff(attempt, ct);
                continue;
            }

            return null;
        }

        return null;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var key = opt.ApiKey.Trim();
        if (!key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = "Bearer " + key;

        req.Headers.TryAddWithoutValidation("Authorization", key);

        if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    // =========================
    // Helpers: choose best prices
    // =========================
    private static decimal LowestUsdtPerXmr(List<EstimateResult> results, decimal amountIn)
    {
        decimal best = 0m;

        foreach (var r in results)
        {
            // r.Amount is XMR received for amountIn USDT
            if (r.Amount <= 0) continue;

            var p = amountIn / r.Amount; // USDT per 1 XMR
            if (p <= 0) continue;

            if (best == 0m || p < best) best = p;
        }

        return best;
    }

    private static bool IsTransient(HttpStatusCode s) =>
        s == HttpStatusCode.RequestTimeout ||
        s == (HttpStatusCode)429 ||
        s == HttpStatusCode.BadGateway ||
        s == HttpStatusCode.ServiceUnavailable ||
        s == HttpStatusCode.GatewayTimeout ||
        s == HttpStatusCode.InternalServerError;

    private static Task Backoff(int attempt, CancellationToken ct)
    {
        var baseMs = attempt switch { 1 => 200, 2 => 600, _ => 1400 };
        var jitter = Random.Shared.Next(0, 150);
        return Task.Delay(baseMs + jitter, ct);
    }

    private static decimal Clamp(decimal v, decimal min, decimal max) =>
        v < min ? min : (v > max ? max : v);

    // =========================
    // Coin / network mapping
    // =========================
    private static string ToCgCoin(AssetRef a)
    {
        var t = (a.ExchangeId ?? a.Ticker).Trim();
        if (string.IsNullOrWhiteSpace(t)) return "";

        // Your app uses USDTTRC etc. CypherGoat likely wants "usdt".
        if (t.StartsWith("USDT", StringComparison.OrdinalIgnoreCase)) return "usdt";
        if (t.StartsWith("USDC", StringComparison.OrdinalIgnoreCase)) return "usdc";

        return t.ToLowerInvariant();
    }

    private static string ToCgNetwork(AssetRef a)
    {
        var n = (a.Network ?? "").Trim();
        var t = (a.Ticker ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(n) || n.Equals("Mainnet", StringComparison.OrdinalIgnoreCase))
        {
            // API examples use mainnet coin code as network (btc/xmr/eth etc)
            return t;
        }

        return n switch
        {
            "Tron" => "trx",
            "Ethereum" => "eth",
            "Binance Smart Chain" => "bsc",
            "Solana" => "sol",
            "Arbitrum" => "arbitrum",
            "Base" => "base",
            "Polygon" => "matic",
            "Avalanche C-Chain" => "avaxc",
            _ => n.ToLowerInvariant()
        };
    }

    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }

    // =========================
    // DTOs
    // =========================
    private sealed class EstimateResponse
    {
        [JsonPropertyName("results")]
        public List<EstimateResult> Results { get; set; } = new();

        [JsonPropertyName("min")]
        public decimal Min { get; set; }
    }

    private sealed class EstimateResult
    {
        [JsonPropertyName("exchange")]
        public string Exchange { get; set; } = "";

        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        [JsonPropertyName("kycScore")]
        public int KycScore { get; set; }
    }
}