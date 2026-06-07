using System.Globalization;
using System.Net;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Xgram exchange client. DIAGNOSTIC BUILD — logs the rate call URL/status/body
/// and dumps the XMR/USDT currency codes from list-currency-options so we can
/// see why the row is empty. grep the console for "[XGRAM]".
///
/// Rate endpoint semantics:
///   GET /retrieve-rate-value?fromCcy=A&toCcy=B&ccyAmount=N
///   response.rate = units of B you receive per 1 unit of A
/// </summary>
public sealed class XgramClient : IXgramClient
{
    private readonly HttpClient _http;
    private readonly XgramOptions opt;

    public string ExchangeKey => "xgram";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public XgramClient(HttpClient http, IOptions<XgramOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    private static void Log(string msg) => Console.WriteLine($"[XGRAM] {msg}");

    private static string Trunc(string? s, int max = 1200)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "…(truncated)";
    }

    // ── SELL: XMR → USDT TRC20 ───────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) { Log("missing ApiKey"); return null; }

        var dto = await GetRateAsync(opt.XmrCode, opt.UsdtCode, ccyAmount: 1m, ct);
        if (dto is null || !dto.Result || dto.Rate <= 0) return null;

        return Result(query, dto.Rate);
    }

    // ── BUY: USDT TRC20 → XMR ────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        var dto = await GetRateAsync(opt.UsdtCode, opt.XmrCode, ccyAmount: opt.BuyProbeAmountUsdt, ct);
        if (dto is null || !dto.Result || dto.Rate <= 0)
        {
            if (dto?.MinFrom is > 0)
            {
                var retryAmount = dto.MinFrom.Value * 1.1m;
                var retry = await GetRateAsync(opt.UsdtCode, opt.XmrCode, retryAmount, ct);
                if (retry is null || !retry.Result || retry.Rate <= 0) return null;
                var retryPrice = retryAmount / retry.Rate;
                if (retryPrice <= 0) return null;
                return Result(query, retryPrice);
            }
            else return null;
        }

        var buyPrice = opt.BuyProbeAmountUsdt / dto.Rate;
        if (buyPrice <= 0) return null;

        return Result(query, buyPrice);
    }

    // ── Currencies ───────────────────────────────────────────────────
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return [];

        using var req = BuildRequest("list-currency-options");
        var res = await SendAsync(req, ct);
        if (res is null) return [];

        try
        {
            using var doc = JsonDocument.Parse(res);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];

            var list = new List<ExchangeCurrency>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var code = prop.Name?.Trim();
                if (string.IsNullOrWhiteSpace(code)) continue;

                var v = prop.Value;
                var available = v.TryGetProperty("available", out var aEl) && aEl.ValueKind == JsonValueKind.True;

                var network = v.TryGetProperty("network", out var nEl) ? nEl.GetString() : null;

                // DIAGNOSTIC: surface every XMR/USDT* code Xgram exposes + availability
                if (code.StartsWith("XMR", StringComparison.OrdinalIgnoreCase) ||
                    code.StartsWith("USDT", StringComparison.OrdinalIgnoreCase))
                    Log($"CCY code='{code}' available={available} network='{network}'");

                if (!available) continue;

                list.Add(new ExchangeCurrency(
                    ExchangeId: code,
                    Ticker: code.ToUpperInvariant(),
                    Network: NormalizeNetwork(network)
                ));
            }

            return list.OrderBy(x => x.Ticker).ThenBy(x => x.Network).ToList();
        }
        catch (Exception ex)
        {
            Log($"PARSE list-currency-options failed: {ex.Message} body={Trunc(res)}");
            return [];
        }
    }

    // ── Internal ─────────────────────────────────────────────────────

    private async Task<RateDto?> GetRateAsync(
        string fromCcy, string toCcy, decimal ccyAmount, CancellationToken ct)
    {
        var qs = $"fromCcy={Uri.EscapeDataString(fromCcy)}" +
                 $"&toCcy={Uri.EscapeDataString(toCcy)}" +
                 $"&ccyAmount={ccyAmount.ToString(CultureInfo.InvariantCulture)}";

        using var req = BuildRequest($"retrieve-rate-value?{qs}");
        Log($"REQ rate from={fromCcy} to={toCcy} amt={ccyAmount.ToString(CultureInfo.InvariantCulture)} url={req.RequestUri}");
        var body = await SendAsync(req, ct);
        if (body is null) return null;

        Log($"RESP rate body={Trunc(body)}");

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var result = root.TryGetProperty("result", out var rEl) && rEl.ValueKind == JsonValueKind.True;
            var rate = root.TryGetProperty("rate", out var dEl) ? ReadDecimal(dEl) : 0m;

            decimal? minFrom = root.TryGetProperty("minFrom", out var minEl) ? ReadDecimalNullable(minEl) : null;
            decimal? maxFrom = root.TryGetProperty("maxFrom", out var maxEl) ? ReadDecimalNullable(maxEl) : null;

            return new RateDto { Result = result, Rate = rate, MinFrom = minFrom, MaxFrom = maxFrom };
        }
        catch (Exception ex)
        {
            Log($"PARSE rate failed: {ex.Message} body={Trunc(body)}");
            return null;
        }
    }

    private HttpRequestMessage BuildRequest(string relativeUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.TryAddWithoutValidation("x-api-key", opt.ApiKey);
        // Prefer JSON but include a wildcard fallback. Sending ONLY "application/json"
        // makes Xgram's (Yii2) content negotiator return 406 "None of your requested
        // content types is supported"; the trailing */* is what browsers send and
        // what the negotiator accepts. The response is still JSON either way.
        req.Headers.TryAddWithoutValidation("Accept", "application/json, */*");
        return req;
    }

    private async Task<string?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var res = await _http.SendAsync(req, cts.Token);
            var body = await res.Content.ReadAsStringAsync(cts.Token);

            if (!res.IsSuccessStatusCode)
            {
                Log($"HTTP {(int)res.StatusCode} {req.RequestUri} body={Trunc(body)}");
                return null;
            }
            return body;
        }
        catch (Exception ex)
        {
            Log($"SEND failed {req.RequestUri}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);

    // ── Helpers ──────────────────────────────────────────────────────

    private static decimal ReadDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private static decimal? ReadDecimalNullable(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Null) return null;
        var d = ReadDecimal(el);
        return d > 0 ? d : null;
    }

    private static string NormalizeNetwork(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return "Mainnet";
        return network.Trim().ToLowerInvariant() switch
        {
            "bitcoin" or "btc" => "Mainnet",
            "monero" or "xmr" => "Mainnet",
            "erc20" or "ethereum" or "eth" => "Ethereum",
            "trc20" or "tron" or "trx" => "Tron",
            "arbitrum" => "Arbitrum",
            "bsc" or "binance smart chain" => "Binance Smart Chain",
            "solana" or "sol" => "Solana",
            "polygon" or "matic" => "Polygon",
            "avaxc" => "Avalanche C-Chain",
            var n => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n)
        };
    }

    private sealed class RateDto
    {
        public bool Result { get; set; }
        public decimal Rate { get; set; }
        public decimal? MinFrom { get; set; }
        public decimal? MaxFrom { get; set; }
    }
}