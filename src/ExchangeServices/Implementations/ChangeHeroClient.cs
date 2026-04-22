using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// ChangeHero API client (JSON-RPC 2.0).
///
/// Endpoint: POST https://api.changehero.io/v2/
/// Auth headers required on every request:
///   api-key: your API key
///   sign:    HMAC-SHA512 of the full JSON request body, keyed with your secret
///
/// SELL (XMR→USDT): getExchangeAmount { from=xmr, to=usdttrc20, amount=1 }
///   → result = USDT per 1 XMR = sell price directly
///
/// BUY (USDT→XMR): getExchangeAmount { from=usdttrc20, to=xmr, amount=probe }
///   → result = XMR received → buyPrice = probe / result
/// </summary>
public sealed class ChangeHeroClient : IChangeHeroClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly ChangeHeroOptions opt;

    public string ExchangeKey => "changehero";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public ChangeHeroClient(HttpClient http, IOptions<ChangeHeroOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── SELL: 1 XMR → USDT ───────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var result = await GetExchangeAmountAsync(opt.XmrCode, opt.UsdtCode, 1m, ct);
        if (result is > 0m)
            return MakeResult(query, result.Value);

        // amount=1 may be below minimum — fetch min and retry
        var min = await GetMinAmountAsync(opt.XmrCode, opt.UsdtCode, ct);
        if (min is null or <= 0m) return null;

        var probe = min.Value * 1.1m;
        var result2 = await GetExchangeAmountAsync(opt.XmrCode, opt.UsdtCode, probe, ct);
        if (result2 is null or <= 0m) return null;

        return MakeResult(query, result2.Value / probe);
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = opt.BuyProbeAmountUsdt;
        var result = await GetExchangeAmountAsync(opt.UsdtCode, opt.XmrCode, probe, ct);

        if (result is null or <= 0m)
        {
            var min = await GetMinAmountAsync(opt.UsdtCode, opt.XmrCode, ct);
            if (min is null or <= 0m) return null;
            probe = min.Value * 1.1m;
            result = await GetExchangeAmountAsync(opt.UsdtCode, opt.XmrCode, probe, ct);
            if (result is null or <= 0m) return null;
        }

        return MakeResult(query, probe / result.Value);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── RPC helpers ───────────────────────────────────────────────────────────

    private async Task<decimal?> GetExchangeAmountAsync(
        string from, string to, decimal amount, CancellationToken ct)
    {
        var body = await RpcAsync("getExchangeAmount", new
        {
            from,
            to,
            amount = amount.ToString(CultureInfo.InvariantCulture)
        }, ct);

        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                Console.WriteLine($"[CHANGEHERO] error: {err}");
                return null;
            }
            if (root.TryGetProperty("result", out var res))
            {
                var v = ParseDecimal(res);
                if (v > 0m) return v;
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHANGEHERO] parse error: {ex.Message} — {body}");
            return null;
        }
    }

    private async Task<decimal?> GetMinAmountAsync(string from, string to, CancellationToken ct)
    {
        var body = await RpcAsync("getMinAmount", new { from, to }, ct);
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var res))
            {
                var v = ParseDecimal(res);
                if (v > 0m) return v;
            }
            return null;
        }
        catch { return null; }
    }

    // ── HTTP / RPC ────────────────────────────────────────────────────────────

    private static int _rpcId = 0;

    private async Task<string?> RpcAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _rpcId).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        });

        // HMAC-SHA512 signature: sign the raw JSON body with the secret key
        var sign = SignPayload(payload, opt.ApiSecret);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        Console.WriteLine($"[CHANGEHERO] {method} → POST {opt.BaseUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, opt.BaseUrl);
            req.Headers.TryAddWithoutValidation("api-key", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("sign", sign);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[CHANGEHERO] Timed out: {method}");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[CHANGEHERO] {method} HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHANGEHERO] Error ({method}): {ex.Message}");
            return null;
        }
    }

    private static string SignPayload(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA512(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
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