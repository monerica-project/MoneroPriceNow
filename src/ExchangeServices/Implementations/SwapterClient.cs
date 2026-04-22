using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Swapter.io API v2 client.
///
/// Auth: X-API-KEY header on estimate and min-amount endpoints.
///
/// SELL (XMR→USDT/TRC20):
///   POST /v2/swap/estimate { deposit:{coin:XMR,network:XMR,amount:1}, withdraw:{coin:USDT,network:TRC20} }
///   → withdraw.amount = USDT received per 1 XMR = sell price directly
///
/// BUY (USDT→XMR):
///   POST /v2/swap/estimate { deposit:{coin:USDT,network:TRC20,amount:probe}, withdraw:{coin:XMR,network:XMR} }
///   → withdraw.amount = XMR received → buyPrice = probe / withdraw.amount
///
/// MinAmountUsd:
///   Sell: minXmr (from /v2/swap/min-amount) × sellPrice = USD minimum
///   Buy:  minUsdt (from /v2/swap/min-amount) = USD minimum directly
///
/// If amount is below minimum, POST /v2/swap/min-amount to get the floor and retry.
/// </summary>
public sealed class SwapterClient : ISwapterClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly SwapterOptions opt;

    public string ExchangeKey => "swapter";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public SwapterClient(HttpClient http, IOptions<SwapterOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── SELL: XMR → USDT ─────────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var min = await GetMinAmountAsync(opt.XmrCoin, opt.XmrNetwork, opt.UsdtCoin, opt.UsdtNetwork, ct);
        var probe = (min is > 0m && min > 1m) ? min.Value * 1.1m : 1m;

        var (withdrawAmt, _) = await EstimateAsync(
            opt.XmrCoin, opt.XmrNetwork, probe,
            opt.UsdtCoin, opt.UsdtNetwork, ct);

        if (withdrawAmt is null or <= 0m) return null;

        // withdrawAmt = USDT received for probe XMR → normalise to per-1-XMR
        var sellPrice = withdrawAmt.Value / probe;

        // min is in XMR → convert to USD using sell price
        decimal? minUsd = min is > 0m ? min.Value * sellPrice : null;
        minUsd ??= opt.MinAmountUsd > 0m ? opt.MinAmountUsd : null;

        return MakeResult(query, sellPrice, minUsd);
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var min = await GetMinAmountAsync(opt.UsdtCoin, opt.UsdtNetwork, opt.XmrCoin, opt.XmrNetwork, ct);
        var probe = opt.BuyProbeAmountUsdt;
        if (min is > 0m && min > probe) probe = min.Value * 1.1m;

        var (withdrawAmt, _) = await EstimateAsync(
            opt.UsdtCoin, opt.UsdtNetwork, probe,
            opt.XmrCoin, opt.XmrNetwork, ct);

        if (withdrawAmt is null or <= 0m) return null;

        // withdrawAmt = XMR received for probe USDT → USDT per 1 XMR
        // min is in USDT = USD directly
        decimal? minUsd = min is > 0m ? min : null;
        minUsd ??= opt.MinAmountUsd > 0m ? opt.MinAmountUsd : null;

        return MakeResult(query, probe / withdrawAmt.Value, minUsd);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core estimate ─────────────────────────────────────────────────────────
    private async Task<(decimal? withdrawAmount, decimal? minimumRequired)> EstimateAsync(
        string depositCoin, string depositNetwork, decimal depositAmount,
        string withdrawCoin, string withdrawNetwork,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            deposit = new { coin = depositCoin, network = depositNetwork, amount = depositAmount },
            withdraw = new { coin = withdrawCoin, network = withdrawNetwork },
            type = "float"
        }, JsonOpt);

        var body = await PostAsync("v2/swap/estimate", payload, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("withdraw", out var wd) &&
                wd.TryGetProperty("amount", out var amtEl))
            {
                var v = ReadDecimal(amtEl);
                if (v > 0m) return (v, null);
            }

            Console.WriteLine($"[SWAPTER] estimate below min or bad response: {body}");
            var min = await GetMinAmountAsync(depositCoin, depositNetwork, withdrawCoin, withdrawNetwork, ct);
            return (null, min);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SWAPTER] parse error: {ex.Message} — {body}");
            return (null, null);
        }
    }

    private async Task<decimal?> GetMinAmountAsync(
        string depositCoin, string depositNetwork,
        string withdrawCoin, string withdrawNetwork,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            deposit = new { coin = depositCoin, network = depositNetwork },
            withdraw = new { coin = withdrawCoin, network = withdrawNetwork }
        }, JsonOpt);

        var body = await PostAsync("v2/swap/min-amount", payload, ct);
        if (body is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("amount", out var el))
            {
                var v = ReadDecimal(el);
                if (v > 0m) return v;
            }
            return null;
        }
        catch { return null; }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> PostAsync(string relativeUrl, string jsonPayload, CancellationToken ct)
    {
        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        Console.WriteLine($"[SWAPTER] POST {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[SWAPTER] Timed out: {fullUrl}");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[SWAPTER] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SWAPTER] Error: {fullUrl} — {ex.Message}");
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

    private static PriceResult MakeResult(PriceQuery q, decimal price, decimal? minAmountUsd = null) =>
        new(q.Base.ToString(), q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null, minAmountUsd);
}