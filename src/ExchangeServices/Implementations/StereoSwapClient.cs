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
/// StereoSwap partner API client.
///
/// Auth: Bearer token in Authorization header.
///
/// POST /partner/v1/exchange/calculate/
/// Body: { amount, last_source:"deposit", type_swap:1, mode:"standard",
///         from_coin, from_network, to_coin, to_network }
/// Response: { receive_amount, min_amount, max_amount, rate }
///   receive_amount = units of to_coin received for `amount` of from_coin
///
/// SELL (XMR→USDT): from=XMR, to=USDT, amount=1
///   → receive_amount = USDT per 1 XMR (direct)
///
/// BUY  (USDT→XMR): from=USDT, to=XMR, amount=probe
///   → receive_amount = XMR received → buyPrice = probe / receive_amount
///
/// If amount < min_amount, retry at min_amount * 1.1
/// </summary>
public sealed class StereoSwapClient : IStereoSwapClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly StereoSwapOptions opt;

    public string ExchangeKey => "stereoswap";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public StereoSwapClient(HttpClient http, IOptions<StereoSwapOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── SELL: XMR → USDT ─────────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (receiveAmt, minAmt) = await CalculateAsync(
            fromCoin: opt.XmrCoin, fromNetwork: opt.XmrNetwork,
            toCoin: opt.UsdtCoin, toNetwork: opt.UsdtNetwork,
            amount: 1m, ct);

        if (receiveAmt is null && minAmt is > 0m)
        {
            var probe = minAmt.Value * 1.1m;
            (receiveAmt, _) = await CalculateAsync(
                opt.XmrCoin, opt.XmrNetwork,
                opt.UsdtCoin, opt.UsdtNetwork,
                probe, ct);
            if (receiveAmt is null or <= 0m) return null;
            return MakeResult(query, receiveAmt.Value / probe);
        }

        if (receiveAmt is null or <= 0m) return null;
        return MakeResult(query, receiveAmt.Value); // amount=1 → direct
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = opt.BuyProbeAmountUsdt;

        var (receiveAmt, minAmt) = await CalculateAsync(
            fromCoin: opt.UsdtCoin, fromNetwork: opt.UsdtNetwork,
            toCoin: opt.XmrCoin, toNetwork: opt.XmrNetwork,
            amount: probe, ct);

        if (receiveAmt is null && minAmt is > 0m)
        {
            probe = minAmt.Value * 1.1m;
            (receiveAmt, _) = await CalculateAsync(
                opt.UsdtCoin, opt.UsdtNetwork,
                opt.XmrCoin, opt.XmrNetwork,
                probe, ct);
        }

        if (receiveAmt is null or <= 0m) return null;
        return MakeResult(query, probe / receiveAmt.Value);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core calculate call ───────────────────────────────────────────────────

    private async Task<(decimal? receiveAmount, decimal? minAmount)> CalculateAsync(
        string fromCoin, string fromNetwork,
        string toCoin, string toNetwork,
        decimal amount, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            amount,
            last_source = "deposit",
            type_swap = opt.TypeSwap,
            mode = opt.Mode,
            from_coin = fromCoin,
            from_network = fromNetwork,
            to_coin = toCoin,
            to_network = toNetwork
        });

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/partner/v1/exchange/calculate/";
        var body = await PostAsync(fullUrl, payload, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var minAmount = root.TryGetProperty("min_amount", out var minEl) ? ReadDecimal(minEl) : 0m;
            if (minAmount <= 0m) minAmount = 0m;

            // If below minimum the API likely returns receive_amount=0 or an error
            if (minAmount > 0m && amount < minAmount)
            {
                Console.WriteLine($"[STEREOSWAP] below min {minAmount} ({fromCoin}→{toCoin})");
                return (null, minAmount);
            }

            if (root.TryGetProperty("receive_amount", out var raEl))
            {
                var v = ReadDecimal(raEl);
                if (v > 0m) return (v, minAmount > 0m ? minAmount : null);
            }

            Console.WriteLine($"[STEREOSWAP] no receive_amount in: {body}");
            return (null, minAmount > 0m ? minAmount : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STEREOSWAP] parse error: {ex.Message} — {body}");
            return (null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> PostAsync(string fullUrl, string jsonPayload, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        Console.WriteLine($"[STEREOSWAP] POST {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            req.Headers.TryAddWithoutValidation("X-API-Key", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[STEREOSWAP] Timed out");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[STEREOSWAP] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STEREOSWAP] Error: {ex.Message}");
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

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}