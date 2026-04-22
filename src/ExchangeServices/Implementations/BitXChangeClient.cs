using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// BitXChange API client.
/// Docs: https://api.bitxchange.io
///
/// Auth: X-API-Key header.
///
/// SELL (XMR→USDT): from=XMR, to=USDT, amount=1       → price      = USDT per 1 XMR
/// BUY  (USDT→XMR): from=USDT, to=XMR, final_amount=1 → from_amount = USDT needed to receive 1 XMR
/// </summary>
public sealed class BitXChangeClient : IBitXChangeClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly CultureInfo InvCulture = CultureInfo.InvariantCulture;

    private readonly HttpClient _http;
    private readonly BitXChangeOptions opt;

    public string ExchangeKey => "bitxchange";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public BitXChangeClient(HttpClient http, IOptions<BitXChangeOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── SELL: XMR → USDT ─────────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (price, _, minDeposit) = await GetPriceAsync(
            from: opt.XmrSymbol, fromNetwork: opt.XmrNetwork,
            to: opt.UsdtSymbol, toNetwork: opt.UsdtNetwork,
            amount: 1m, finalAmount: null, ct);

        if (price is null && minDeposit is > 0m)
        {
            var probe = minDeposit.Value * 1.1m;
            (price, _, _) = await GetPriceAsync(
                opt.XmrSymbol, opt.XmrNetwork,
                opt.UsdtSymbol, opt.UsdtNetwork,
                amount: probe, finalAmount: null, ct);
            if (price is null or <= 0m) return null;
            return MakeResult(query, price.Value / probe);
        }

        if (price is null or <= 0m) return null;
        return MakeResult(query, price.Value);
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        // final_amount=1 → "I want to receive 1 XMR"
        // API returns from_amount = USDT you need to send — that's our buy price.
        var (_, fromAmount, _) = await GetPriceAsync(
            from: opt.UsdtSymbol, fromNetwork: opt.UsdtNetwork,
            to: opt.XmrSymbol, toNetwork: opt.XmrNetwork,
            amount: null, finalAmount: 1m, ct);

        if (fromAmount is null or <= 0m) return null;
        return MakeResult(query, fromAmount.Value);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core price call ───────────────────────────────────────────────────────
    // Returns (price, fromAmount, minDeposit).
    private async Task<(decimal? price, decimal? fromAmount, decimal? minDeposit)> GetPriceAsync(
        string from, string fromNetwork,
        string to, string toNetwork,
        decimal? amount, decimal? finalAmount,
        CancellationToken ct)
    {
        var qs = $"from={Uri.EscapeDataString(from)}" +
                 $"&to={Uri.EscapeDataString(to)}" +
                 $"&type=variable";

        if (amount.HasValue)
            qs += $"&amount={amount.Value.ToString(InvCulture)}";

        if (finalAmount.HasValue)
            qs += $"&final_amount={finalAmount.Value.ToString(InvCulture)}";

        if (!string.IsNullOrWhiteSpace(fromNetwork))
            qs += $"&from_network={Uri.EscapeDataString(fromNetwork)}";
        if (!string.IsNullOrWhiteSpace(toNetwork))
            qs += $"&to_network={Uri.EscapeDataString(toNetwork)}";

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/price?{qs}";
        var body = await GetAsync(fullUrl, ct);
        if (body is null) return (null, null, null);

        try
        {
            var debugBody = body; // breakpoint here to inspect raw JSON

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var price = root.TryGetProperty("price", out var pEl) ? ReadDecimal(pEl) : 0m;
            var fromAmount = root.TryGetProperty("from_amount", out var faEl) ? ReadDecimal(faEl) : 0m;
            var minDeposit = root.TryGetProperty("min_deposit", out var minEl) ? ReadDecimal(minEl) : 0m;

            if (price <= 0m && fromAmount <= 0m)
            {
                Console.WriteLine($"[BITXCHANGE] zero price for {from}→{to} amount={amount} final_amount={finalAmount}: {body}");
                return (null, null, minDeposit > 0m ? minDeposit : null);
            }

            if (amount.HasValue && minDeposit > 0m && amount.Value < minDeposit)
                return (null, null, minDeposit);

            return (price > 0m ? price : null,
                    fromAmount > 0m ? fromAmount : null,
                    minDeposit > 0m ? minDeposit : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITXCHANGE] parse error: {ex.Message} — {body}");
            return (null, null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private async Task<string?> GetAsync(string fullUrl, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        Console.WriteLine($"[BITXCHANGE] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("X-API-Key", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[BITXCHANGE] Timed out");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[BITXCHANGE] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITXCHANGE] Error: {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static decimal ReadDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, InvCulture, out var ds)) return ds;
        return 0m;
    }

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}