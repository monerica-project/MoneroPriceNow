using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// CypherGoat API client — crypto exchange aggregator.
/// Docs: https://api.cyphergoat.com
///
/// Auth: Authorization: Bearer YOUR_API_KEY
///
/// GET /estimate?coin1=xmr&coin2=usdt&amount=1&network1=xmr&network2=trc20&best=true
/// Response: { results: [{ exchange, amount, kycScore }], min, tradeValue_fiat, ... }
///   results[0].amount = units of coin2 received for `amount` of coin1
///   (best=true returns only the top provider)
///
/// SELL (XMR→USDT): coin1=xmr, coin2=usdt, amount=1
///   → results[0].amount = USDT per 1 XMR (direct sell price)
///
/// BUY  (USDT→XMR): coin1=usdt, coin2=xmr, amount=probe
///   → results[0].amount = XMR received → buyPrice = probe / amount
///
/// MinAmountUsd = min * (tradeValue_fiat / depositAmount)
///
/// If amount < min, retry at min * 1.1
/// </summary>
public sealed class CypherGoatClient : ICypherGoatClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly CypherGoatOptions opt;

    public string ExchangeKey => "cyphergoat";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public CypherGoatClient(HttpClient http, IOptions<CypherGoatOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── SELL: XMR → USDT ─────────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (amount, min, tvFiat) = await EstimateAsync(
            coin1: opt.XmrCoin, network1: opt.XmrNetwork,
            coin2: opt.UsdtCoin, network2: opt.UsdtNetwork,
            depositAmount: 1m, ct);

        if (amount is null && min is > 0m)
        {
            var probe = min.Value * 1.1m;
            (amount, _, tvFiat) = await EstimateAsync(
                opt.XmrCoin, opt.XmrNetwork,
                opt.UsdtCoin, opt.UsdtNetwork,
                probe, ct);
            if (amount is null or <= 0m) return null;
            return MakeResult(query, amount.Value / probe, CalcMinUsd(min, tvFiat, probe));
        }

        if (amount is null or <= 0m) return null;
        return MakeResult(query, amount.Value, CalcMinUsd(min, tvFiat, 1m));
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = opt.BuyProbeAmountUsdt;

        var (amount, min, tvFiat) = await EstimateAsync(
            coin1: opt.UsdtCoin, network1: opt.UsdtNetwork,
            coin2: opt.XmrCoin, network2: opt.XmrNetwork,
            depositAmount: probe, ct);

        if (amount is null && min is > 0m)
        {
            probe = min.Value * 1.1m;
            (amount, _, tvFiat) = await EstimateAsync(
                opt.UsdtCoin, opt.UsdtNetwork,
                opt.XmrCoin, opt.XmrNetwork,
                probe, ct);
        }

        if (amount is null or <= 0m) return null;
        return MakeResult(query, probe / amount.Value, CalcMinUsd(min, tvFiat, probe));
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core estimate call ────────────────────────────────────────────────────
    // Returns (bestAmount, minAmount, tradeValueFiat).
    // bestAmount     = best exchange's output amount for depositAmount of coin1.
    // minAmount      = minimum deposit; bestAmount is null when below it.
    // tradeValueFiat = USD value of depositAmount of coin1 (used to derive MinAmountUsd).

    private async Task<(decimal? bestAmount, decimal? minAmount, decimal? tradeValueFiat)> EstimateAsync(
        string coin1, string network1,
        string coin2, string network2,
        decimal depositAmount, CancellationToken ct)
    {
        var qs = $"coin1={Uri.EscapeDataString(coin1.ToLowerInvariant())}" +
                 $"&coin2={Uri.EscapeDataString(coin2.ToLowerInvariant())}" +
                 $"&amount={depositAmount.ToString(CultureInfo.InvariantCulture)}" +
                 $"&network1={Uri.EscapeDataString(network1.ToLowerInvariant())}" +
                 $"&network2={Uri.EscapeDataString(network2.ToLowerInvariant())}" +
                 $"&best=true";

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/estimate?{qs}";
        var body = await GetAsync(fullUrl, ct);
        if (body is null) return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Parse minimum deposit
            decimal? min = root.TryGetProperty("min", out var minEl) ? ReadDecimal(minEl) : null;
            if (min <= 0m) min = null;

            // Parse trade value in fiat (USD value of the requested depositAmount)
            decimal? tvFiat = root.TryGetProperty("tradeValue_fiat", out var tvEl) ? ReadDecimal(tvEl) : null;
            if (tvFiat <= 0m) tvFiat = null;

            if (min is > 0m && depositAmount < min.Value)
            {
                Console.WriteLine($"[CYPHERGOAT] below min {min} for {coin1}→{coin2} amount={depositAmount}");
                return (null, min, tvFiat);
            }

            // Format 1: { "results": [{ "amount": 323.661, ... }] }  (docs format)
            if (root.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (item.TryGetProperty("amount", out var amtEl))
                    {
                        var v = ReadDecimal(amtEl);
                        if (v > 0m) return (v, min, tvFiat);
                    }
                }
            }

            // Format 2: { "rates": { "Amount": 323.661, "ExchangeName": "FixedFloat" } }
            if (root.TryGetProperty("rates", out var rates))
            {
                if (rates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in new[] { "Amount", "amount" })
                    {
                        if (rates.TryGetProperty(prop, out var amtEl))
                        {
                            var v = ReadDecimal(amtEl);
                            if (v > 0m) return (v, min, tvFiat);
                        }
                    }
                }
                else if (rates.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rates.EnumerateArray())
                    {
                        foreach (var prop in new[] { "Amount", "amount" })
                        {
                            if (item.TryGetProperty(prop, out var amtEl))
                            {
                                var v = ReadDecimal(amtEl);
                                if (v > 0m) return (v, min, tvFiat);
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"[CYPHERGOAT] no usable amount in: {body}");
            return (null, min, tvFiat);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CYPHERGOAT] parse error: {ex.Message} — {body}");
            return (null, null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> GetAsync(string fullUrl, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        Console.WriteLine($"[CYPHERGOAT] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opt.ApiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[CYPHERGOAT] Timed out");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[CYPHERGOAT] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CYPHERGOAT] Error: {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static decimal ReadDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private static decimal? CalcMinUsd(decimal? min, decimal? tradeValueFiat, decimal depositAmount) =>
        min is > 0m && tradeValueFiat is > 0m && depositAmount > 0m
            ? min.Value * (tradeValueFiat.Value / depositAmount)
            : null;

    private PriceResult MakeResult(PriceQuery q, decimal price, decimal? minAmountUsd = null) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null, minAmountUsd);
}