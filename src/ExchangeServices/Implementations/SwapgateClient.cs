using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Swapgate.io client.
///
/// Rate endpoint (no auth required):
///   GET /api/v1/rates/public/one
///     ?instrumentFromCurrencyTitle=XMR
///     &instrumentFromNetworkTitle=XMR
///     &instrumentToCurrencyTitle=USDT
///     &instrumentToNetworkTitle=TRC20
///     &rateMode=FLOATING
///     &claimedDepositAmount=1
///     &markup=0
///
///   response.amountToGet = units of instrumentTo received for claimedDepositAmount of instrumentFrom
///
/// SELL (XMR→USDT): claimedDepositAmount=1 XMR → amountToGet = USDT per 1 XMR = sell price
/// BUY  (USDT→XMR): claimedDepositAmount=probe USDT → amountToGet = XMR received → buyPrice = probe / amountToGet
/// </summary>
public sealed class SwapgateClient : ISwapgateClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly SwapgateOptions opt;

    public string ExchangeKey => "swapgate";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public SwapgateClient(HttpClient http, IOptions<SwapgateOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── SELL: XMR → USDT ───────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = opt.SellProbeAmountXmr;

        var (amountToGet, minRequired) = await GetRateWithMinAsync(
            fromCurrency: opt.XmrCurrency,
            fromNetwork: opt.XmrNetwork,
            toCurrency: opt.UsdtCurrency,
            toNetwork: opt.UsdtNetwork,
            depositAmount: probe,
            ct);

        if (amountToGet is null && minRequired is > 0m)
        {
            probe = minRequired.Value * 1.1m;
            (amountToGet, _) = await GetRateWithMinAsync(
                fromCurrency: opt.XmrCurrency,
                fromNetwork: opt.XmrNetwork,
                toCurrency: opt.UsdtCurrency,
                toNetwork: opt.UsdtNetwork,
                depositAmount: probe,
                ct);
        }

        if (amountToGet is null || amountToGet <= 0m) return null;

        var sellPrice = amountToGet.Value / probe;
        if (sellPrice <= 0m) return null;

        return MakeResult(query, sellPrice);
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = opt.BuyProbeAmountUsdt;

        var (amountToGet, minRequired) = await GetRateWithMinAsync(
            fromCurrency: opt.UsdtCurrency,
            fromNetwork: opt.UsdtNetwork,
            toCurrency: opt.XmrCurrency,
            toNetwork: opt.XmrNetwork,
            depositAmount: probe,
            ct);

        // If probe was below minimum, retry with 110% of the required minimum
        if (amountToGet is null && minRequired is > 0m)
        {
            probe = minRequired.Value * 1.1m;
            (amountToGet, _) = await GetRateWithMinAsync(
                fromCurrency: opt.UsdtCurrency,
                fromNetwork: opt.UsdtNetwork,
                toCurrency: opt.XmrCurrency,
                toNetwork: opt.XmrNetwork,
                depositAmount: probe,
                ct);
        }

        if (amountToGet is null || amountToGet <= 0m) return null;

        // amountToGet = XMR received for probe USDT → divide to get USDT per 1 XMR
        var buyPrice = probe / amountToGet.Value;
        if (buyPrice <= 0m) return null;

        return MakeResult(query, buyPrice);
    }

    // ── Currencies (not required — no auth needed, skip for simplicity) ───────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core rate call ────────────────────────────────────────────────────────
    // Returns (amountToGet, minRequired).
    // If probe is below minimum, amountToGet=null and minRequired is populated
    // from the 422 body so the caller can retry with a larger amount.

    private async Task<(decimal? amountToGet, decimal? minRequired)> GetRateWithMinAsync(
        string fromCurrency, string fromNetwork,
        string toCurrency, string toNetwork,
        decimal depositAmount, CancellationToken ct)
    {
        var url = $"api/v1/rates/public/one" +
                  $"?instrumentFromCurrencyTitle={Uri.EscapeDataString(fromCurrency)}" +
                  $"&instrumentFromNetworkTitle={Uri.EscapeDataString(fromNetwork)}" +
                  $"&instrumentToCurrencyTitle={Uri.EscapeDataString(toCurrency)}" +
                  $"&instrumentToNetworkTitle={Uri.EscapeDataString(toNetwork)}" +
                  $"&rateMode=FLOATING" +
                  $"&claimedDepositAmount={depositAmount.ToString(CultureInfo.InvariantCulture)}" +
                  $"&markup=0";

        var (body, statusCode) = await SendAsync(url, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (statusCode == 422)
            {
                decimal? min = null;
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("details", out var details) &&
                    details.TryGetProperty("expected", out var expEl))
                {
                    var v = ReadDecimal(expEl);
                    if (v > 0m) min = v;
                }
                Console.WriteLine($"[SWAPGATE] 422 min={min} for {fromCurrency}→{toCurrency}");
                return (null, min);
            }

            if (root.TryGetProperty("amountToGet", out var el))
            {
                var v = ReadDecimal(el);
                if (v > 0m) return (v, null);
            }
            if (root.TryGetProperty("price", out var pel))
            {
                var v = ReadDecimal(pel);
                if (v > 0m) return (v, null);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<(string? body, int statusCode)> SendAsync(string relativeUrl, CancellationToken ct)
    {
        var baseUrl = opt.BaseUrl.TrimEnd('/');
        var fullUrl = $"{baseUrl}/{relativeUrl.TrimStart('/')}";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        Console.WriteLine($"[SWAPGATE] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[SWAPGATE] Timed out: {fullUrl}");
                return (null, 0);
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);
            var code = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode && code != 422)
            {
                Console.WriteLine($"[SWAPGATE] HTTP {code}: {body}");
                return (null, code);
            }

            return (body, code);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SWAPGATE] Error: {fullUrl} — {ex.Message}");
            return (null, 0);
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

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}