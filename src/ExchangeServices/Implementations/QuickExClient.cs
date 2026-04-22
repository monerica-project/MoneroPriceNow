using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Quickex.io API v1 client.
/// No API key required — public endpoint.
/// Blocked by WAF if User-Agent looks like a bot, so we send a browser UA.
///
/// SELL (XMR→USDT): probe XMR, amountToGet = USDT → sellPrice = amountToGet / probe
/// BUY  (USDT→XMR): probe USDT, amountToGet = XMR → buyPrice = probe / amountToGet
/// 422 → parse data.details.expected for minimum, retry at 110%
/// </summary>
public sealed class QuickexClient : IQuickexClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Mimic a real browser — Quickex WAF blocks dotnet/httpclient UA strings
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly QuickexOptions opt;

    public string ExchangeKey => "quickex";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public QuickexClient(HttpClient http, IOptions<QuickexOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── SELL: XMR → USDT ─────────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = opt.SellProbeAmountXmr;

        var (amt, min) = await FetchRateAsync(
            opt.XmrCurrency, opt.XmrNetwork,
            opt.UsdtCurrency, opt.UsdtNetwork,
            probe, opt.XmrCurrency, ct);

        if (amt is null && min is > 0m)
        {
            probe = min.Value * 1.1m;
            (amt, _) = await FetchRateAsync(
                opt.XmrCurrency, opt.XmrNetwork,
                opt.UsdtCurrency, opt.UsdtNetwork,
                probe, opt.XmrCurrency, ct);
        }

        if (amt is null || amt <= 0m) return null;
        return MakeResult(query, amt.Value / probe);
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = opt.BuyProbeAmountUsdt;

        var (amt, min) = await FetchRateAsync(
            opt.UsdtCurrency, opt.UsdtNetwork,
            opt.XmrCurrency, opt.XmrNetwork,
            probe, opt.UsdtCurrency, ct);

        if (amt is null && min is > 0m)
        {
            probe = min.Value * 1.1m;
            (amt, _) = await FetchRateAsync(
                opt.UsdtCurrency, opt.UsdtNetwork,
                opt.XmrCurrency, opt.XmrNetwork,
                probe, opt.UsdtCurrency, ct);
        }

        if (amt is null || amt <= 0m) return null;
        return MakeResult(query, probe / amt.Value);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core fetch ────────────────────────────────────────────────────────────

    private async Task<(decimal? amountToGet, decimal? minRequired)> FetchRateAsync(
        string fromCcy, string fromNet,
        string toCcy, string toNet,
        decimal depositAmount, string depositCcy,
        CancellationToken ct)
    {
        var qs = $"exchangeType=crypto" +
                 $"&instrumentFromCurrencyTitle={Uri.EscapeDataString(fromCcy)}" +
                 $"&instrumentFromNetworkTitle={Uri.EscapeDataString(fromNet)}" +
                 $"&instrumentToCurrencyTitle={Uri.EscapeDataString(toCcy)}" +
                 $"&instrumentToNetworkTitle={Uri.EscapeDataString(toNet)}" +
                 $"&claimedDepositAmount={depositAmount.ToString(CultureInfo.InvariantCulture)}" +
                 $"&claimedDepositAmountCurrency={Uri.EscapeDataString(depositCcy)}" +
                 $"&rateMode=FLOATING" +
                 $"&markup=0";

        if (!string.IsNullOrWhiteSpace(opt.ReferrerId))
            qs += $"&referrerId={Uri.EscapeDataString(opt.ReferrerId)}";

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/api/v1/rates/public/one?{qs}";

        var (body, code) = await GetAsync(fullUrl, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (code == 422)
            {
                decimal? min = null;
                if (root.TryGetProperty("data", out var d) &&
                    d.TryGetProperty("details", out var det) &&
                    det.TryGetProperty("expected", out var expEl))
                {
                    var v = ParseDecimal(expEl);
                    if (v > 0m) min = v;
                }
                Console.WriteLine($"[QUICKEX] 422 min={min} ({fromCcy}→{toCcy})");
                return (null, min);
            }

            foreach (var field in new[] { "amountToGet", "price" })
                if (root.TryGetProperty(field, out var el))
                {
                    var v = ParseDecimal(el);
                    if (v > 0m) return (v, null);
                }

            Console.WriteLine($"[QUICKEX] No rate field in: {body}");
            return (null, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QUICKEX] Parse error: {ex.Message}");
            return (null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<(string? body, int code)> GetAsync(string fullUrl, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        Console.WriteLine($"[QUICKEX] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);

            // Must look like a browser or Quickex WAF returns 403
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Referer", "https://quickex.io/");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[QUICKEX] Timed out");
                return (null, 0);
            }

            using var resp = await sendTask;
            var code = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            if (!resp.IsSuccessStatusCode && code != 422)
            {
                Console.WriteLine($"[QUICKEX] HTTP {code}: {body}");
                return (null, code);
            }

            return (body, code);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QUICKEX] Error: {ex.Message}");
            return (null, 0);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static decimal ParseDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}