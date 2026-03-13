using System.Globalization;
using System.Net;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Xgram exchange client.
/// API docs: https://xgram.io/api/documentation
///
/// Rate endpoint semantics:
///   GET /retrieve-rate-value?fromCcy=A&toCcy=B&ccyAmount=N
///   response.rate = units of B you receive per 1 unit of A
///
/// SELL (XMR → USDT): fromCcy=XMR, toCcy=USDTTRC20, ccyAmount=1
///   → sellPrice = rate  (USDT per 1 XMR)
///
/// BUY  (USDT → XMR):  fromCcy=USDTTRC20, toCcy=XMR, ccyAmount=1
///   → rate = XMR per 1 USDT → buyPrice = 1 / rate  (USDT per 1 XMR)
/// </summary>
public sealed class XgramClient : IXgramClient
{
    private readonly HttpClient _http;
    private readonly XgramOptions _opt;

    public string ExchangeKey => "xgram";
    public string SiteName => _opt.SiteName;
    public string? SiteUrl => _opt.SiteUrl;
    public char PrivacyLevel => _opt.PrivacyLevel;

    public XgramClient(HttpClient http, IOptions<XgramOptions> options)
    {
        _http = http;
        _opt = options.Value;
    }

    // ── SELL: XMR → USDT TRC20 ───────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) return null;

        var dto = await GetRateAsync(_opt.XmrCode, _opt.UsdtCode, ccyAmount: 1m, ct);
        if (dto is null || !dto.Result || dto.Rate <= 0) return null;

        // rate = USDT per 1 XMR — that is the sell price directly
        return Result(query, dto.Rate);
    }

    // ── BUY: USDT TRC20 → XMR ────────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) return null;

        // ccyAmount=1 USDT is below minFrom — use a realistic probe amount.
        // rate = XMR per 1 USDT → buyPrice (USDT per 1 XMR) = probeAmount / (probeAmount * rate) = 1 / rate
        var dto = await GetRateAsync(_opt.UsdtCode, _opt.XmrCode, ccyAmount: _opt.BuyProbeAmountUsdt, ct);
        if (dto is null || !dto.Result || dto.Rate <= 0)
        {
            // If minFrom was returned, retry with that amount
            if (dto?.MinFrom is > 0)
            {
                var retryAmount = dto.MinFrom.Value * 1.1m;
                var retry = await GetRateAsync(_opt.UsdtCode, _opt.XmrCode, retryAmount, ct);
                if (retry is null || !retry.Result || retry.Rate <= 0) return null;
                var retryPrice = retryAmount / retry.Rate;
                if (retryPrice <= 0) return null;
                return Result(query, retryPrice);
            }
            else return null;
        }

        var buyPrice = _opt.BuyProbeAmountUsdt / dto.Rate;
        if (buyPrice <= 0) return null;

        return Result(query, buyPrice);
    }

    // ── Currencies ───────────────────────────────────────────────────
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey)) return [];

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
                if (!available) continue;

                var network = v.TryGetProperty("network", out var nEl) ? nEl.GetString() : null;

                list.Add(new ExchangeCurrency(
                    ExchangeId: code,
                    Ticker: code.ToUpperInvariant(),
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

    // ── Internal ─────────────────────────────────────────────────────

    private async Task<RateDto?> GetRateAsync(
        string fromCcy, string toCcy, decimal ccyAmount, CancellationToken ct)
    {
        var qs = $"fromCcy={Uri.EscapeDataString(fromCcy)}" +
                 $"&toCcy={Uri.EscapeDataString(toCcy)}" +
                 $"&ccyAmount={ccyAmount.ToString(CultureInfo.InvariantCulture)}";

        using var req = BuildRequest($"retrieve-rate-value?{qs}");
        var body = await SendAsync(req, ct);
        if (body is null) return null;

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
        catch
        {
            return null;
        }
    }

    private HttpRequestMessage BuildRequest(string relativeUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.TryAddWithoutValidation("x-api-key", _opt.ApiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        return req;
    }

    private async Task<string?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(_opt.RequestTimeoutSeconds, 2, 30));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var res = await _http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadAsStringAsync(cts.Token);
        }
        catch
        {
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