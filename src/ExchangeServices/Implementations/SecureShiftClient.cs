using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// SecureShift Partner API v3 client.
/// https://secureshift.io/api/v3
///
/// Auth: x-api-key header on all requests.
///
/// Endpoint used:
///   GET /get-estimate?currency_from=&network_from=&currency_to=&network_to=&amount=
///   Returns { estimated_amount: "0.293" } — units of currency_to received for `amount` of currency_from.
///
/// SELL (XMR → USDT/TRC20): currency_from=xmr, network_from=xmr, currency_to=usdt, network_to=trc20, amount=1
///   → estimatedAmount = USDT received per 1 XMR = sell price (direct)
///
/// BUY  (USDT → XMR): currency_from=usdt, network_from=trc20, currency_to=xmr, network_to=xmr, amount=probeUsdt
///   → estimatedAmount = XMR received for probeUsdt → buyPrice = probeUsdt / estimatedAmount
/// </summary>
public sealed class SecureShiftClient : ISecureShiftClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly SecureShiftOptions _opt;

    public string ExchangeKey => "secureshift";
    public string SiteName => _opt.SiteName;
    public string? SiteUrl => _opt.SiteUrl;
    public char PrivacyLevel => _opt.PrivacyLevel;

    public SecureShiftClient(HttpClient http, IOptions<SecureShiftOptions> options)
    {
        _http = http;
        _opt = options.Value;
    }

    // ── SELL: 1 XMR → USDT ───────────────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        // amount=1 XMR; response = USDT received = sell price directly
        var estimated = await GetEstimateAsync(
            currencyFrom: _opt.XmrSymbol,
            networkFrom: _opt.XmrNetwork,
            currencyTo: _opt.UsdtSymbol,
            networkTo: _opt.UsdtNetwork,
            amount: 1m,
            ct);

        if (estimated is null || estimated <= 0m) return null;

        return MakeResult(query, estimated.Value);
    }

    // ── BUY: USDT → XMR ──────────────────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        // Probe with a realistic USDT amount to avoid min/max issues.
        // estimated = XMR received for probeUsdt → buyPrice = probeUsdt / estimated
        var probe = _opt.BuyProbeAmountUsdt;
        var estimated = await GetEstimateAsync(
            currencyFrom: _opt.UsdtSymbol,
            networkFrom: _opt.UsdtNetwork,
            currencyTo: _opt.XmrSymbol,
            networkTo: _opt.XmrNetwork,
            amount: probe,
            ct);

        if (estimated is null || estimated <= 0m) return null;

        var buyPrice = probe / estimated.Value;
        if (buyPrice <= 0m) return null;

        return MakeResult(query, buyPrice);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        // GET /get-pairs?symbol=xmr&network=xmr — returns what XMR can pair with
        var qs = $"get-pairs?symbol={Uri.EscapeDataString(_opt.XmrSymbol)}&network={Uri.EscapeDataString(_opt.XmrNetwork)}";
        var body = await SendAsync(qs, ct);
        if (body is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Response is an array of currency objects or a wrapper — handle both
            var arr = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("data", out var d) ? d : root;

            if (arr.ValueKind != JsonValueKind.Array) return [];

            var list = new List<ExchangeCurrency>();
            foreach (var item in arr.EnumerateArray())
            {
                var symbol = GetString(item, "symbol", "currency", "code");
                var network = GetString(item, "network");
                if (string.IsNullOrWhiteSpace(symbol)) continue;

                list.Add(new ExchangeCurrency(
                    ExchangeId: $"{symbol}:{network}".ToLowerInvariant(),
                    Ticker: symbol.ToUpperInvariant(),
                    Network: NormalizeNetwork(network)
                ));
            }

            return list.OrderBy(x => x.Ticker).ThenBy(x => x.Network).ToList();
        }
        catch
        {
            return [];
        }
    }

    // ── Core estimate call ────────────────────────────────────────────────────

    private async Task<decimal?> GetEstimateAsync(
        string currencyFrom, string networkFrom,
        string currencyTo, string networkTo,
        decimal amount, CancellationToken ct)
    {
        var qs = $"get-estimate" +
                 $"?currency_from={Uri.EscapeDataString(currencyFrom)}" +
                 $"&network_from={Uri.EscapeDataString(networkFrom)}" +
                 $"&currency_to={Uri.EscapeDataString(currencyTo)}" +
                 $"&network_to={Uri.EscapeDataString(networkTo)}" +
                 $"&amount={amount.ToString(CultureInfo.InvariantCulture)}";

        var body = await SendAsync(qs, ct);
        if (body is null) return null;

        Console.WriteLine($"[SECURESHIFT] estimate raw: {body}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Try common field names
            foreach (var field in new[] { "estimated_amount", "estimatedAmount", "amount", "result" })
            {
                if (root.TryGetProperty(field, out var el))
                {
                    var v = ReadDecimal(el);
                    if (v > 0m) return v;
                }
            }

            // Some APIs wrap in a data object
            if (root.TryGetProperty("data", out var data))
            {
                foreach (var field in new[] { "estimated_amount", "estimatedAmount", "amount" })
                {
                    if (data.TryGetProperty(field, out var el))
                    {
                        var v = ReadDecimal(el);
                        if (v > 0m) return v;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> SendAsync(string relativeUrl, CancellationToken ct)
    {
        var baseUrl = _opt.BaseUrl.TrimEnd('/');
        var fullUrl = $"{baseUrl}/{relativeUrl.TrimStart('/')}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("x-api-key", _opt.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            // HttpClient.Timeout (set in registration) is what reliably kills
            // hung TCP connections — CancellationToken alone is not enough.
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(CancellationToken.None);
                Console.WriteLine($"[SECURESHIFT] HTTP {(int)resp.StatusCode}: {err}");
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);
            Console.WriteLine($"[SECURESHIFT] {fullUrl} → {body}");
            return body;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine($"[SECURESHIFT] Timeout: {fullUrl}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SECURESHIFT] Error: {fullUrl} — {ex.Message}");
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

    private static string? GetString(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
            if (el.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        return null;
    }

    private static string NormalizeNetwork(string? network) =>
        network?.Trim().ToLowerInvariant() switch
        {
            "trc20" or "tron" or "trx" => "Tron",
            "erc20" or "eth" => "Ethereum",
            "btc" or "bitcoin" => "Mainnet",
            "xmr" or "monero" => "Mainnet",
            null or "" => "Mainnet",
            var n => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n)
        };

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}