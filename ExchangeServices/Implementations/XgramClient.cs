using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class XgramClient : IXgramClient
{
    private readonly HttpClient http;
    private readonly XgramOptions opt;

    public string  ExchangeKey => "xgram";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;

    public XgramClient(HttpClient http, IOptions<XgramOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ==========================================
    // SELL: 1 XMR -> ? USDT(TRON)
    // Uses: GET /retrieve-rate-value?fromCcy=...&toCcy=...&ccyAmount=1
    // rate = "to per 1 from" (per their BTC->ETH example)
    // ==========================================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var fromCodes = PreferExchangeIdFirst(GetCodeCandidates(query.Base), query.Base.ExchangeId);
        var toCodes = PreferExchangeIdFirst(GetCodeCandidates(query.Quote), query.Quote.ExchangeId);

        foreach (var from in fromCodes)
            foreach (var to in toCodes)
            {
                var dto = await GetRateAsync(from, to, amountFrom: 1m, ct);
                if (dto is null || !dto.Result || dto.Rate <= 0) continue;

                // SELL price shown as USDT per 1 XMR
                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: dto.Rate,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }

        return null;
    }

    // ==========================================
    // BUY: ? USDT needed to receive ~1 XMR
    // We quote USDT->XMR for a valid amount (>= minFrom), then compute:
    //   xmrOut = amountFrom * rate
    //   usdtPerXmr = amountFrom / xmrOut
    // Refine once using the computed USDT needed for ~1 XMR
    // ==========================================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var usdt = query.Quote;
        var xmr = query.Base;

        var fromCodes = PreferExchangeIdFirst(GetCodeCandidates(usdt), usdt.ExchangeId);
        var toCodes = PreferExchangeIdFirst(GetCodeCandidates(xmr), xmr.ExchangeId);

        foreach (var from in fromCodes) // from = USDT*
            foreach (var to in toCodes)     // to   = XMR
            {
                // First call: figure out min/max + a usable starting amount
                var firstProbe = await GetRateAsync(from, to, amountFrom: 1m, ct);
                if (firstProbe is null || !firstProbe.Result) continue;

                var min = firstProbe.MinFrom ?? 0m;
                var max = firstProbe.MaxFrom ?? 0m;

                // choose a safe amount >= min (Xgram enforces minFrom)
                var start = 50m;
                if (min > 0) start = Math.Max(start, min * 1.25m);
                if (max > 0) start = Math.Min(start, max);

                if (start <= 0 || (max > 0 && start > max)) continue;

                var first = await GetRateAsync(from, to, start, ct);
                if (first is null || !first.Result || first.Rate <= 0) continue;

                // rate = XMR per 1 USDT (because from=USDT, to=XMR)
                var xmrOut1 = start * first.Rate;
                if (xmrOut1 <= 0) continue;

                var p1 = start / xmrOut1; // USDT per XMR
                if (p1 <= 0) continue;

                // refine: amount of USDT to target ~1 XMR
                var refine = p1;

                // clamp refine inside min/max (if provided)
                if (min > 0 && refine < min) refine = min * 1.25m;
                if (max > 0 && refine > max) refine = max;

                var second = await GetRateAsync(from, to, refine, ct);
                if (second is null || !second.Result || second.Rate <= 0)
                {
                    // fallback to p1 if refine fails
                    return new PriceResult(ExchangeKey, query.Base, query.Quote, p1, DateTimeOffset.UtcNow, null, null);
                }

                var xmrOut2 = refine * second.Rate;
                if (xmrOut2 <= 0) return new PriceResult(ExchangeKey, query.Base, query.Quote, p1, DateTimeOffset.UtcNow, null, null);

                var p2 = refine / xmrOut2;
                if (p2 <= 0) continue;

                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: p2,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }

        return null;
    }

    // ==========================================
    // CURRENCIES: GET /list-currency-options
    // Response is an object map: { "ARB": {...}, "USDTTRC": {...}, ... }
    // Key is the code used in fromCcy/toCcy.
    // ==========================================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return Array.Empty<ExchangeCurrency>();

        using var req = new HttpRequestMessage(HttpMethod.Get, "list-currency-options");
        AddAuth(req);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        var res = await Http.SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
        if (res is null) return Array.Empty<ExchangeCurrency>();
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices) return Array.Empty<ExchangeCurrency>();

        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var code = prop.Name?.Trim();
                if (string.IsNullOrWhiteSpace(code)) continue;

                var v = prop.Value;
                var available = v.TryGetProperty("available", out var aEl) && aEl.ValueKind == JsonValueKind.True;

                // If they ever include disabled coins, you can skip non-available
                if (!available) continue;

                var network = v.TryGetProperty("network", out var nEl) ? nEl.GetString() : null;
                var netNorm = NormalizeNetwork(network);

                // tagname non-empty => memo/tag required (you can store later if you want)
                list.Add(new ExchangeCurrency(
                    ExchangeId: code,                 // EXACT code for fromCcy/toCcy
                    Ticker: code.ToUpperInvariant(),
                    Network: netNorm
                ));
            }

            return list
                .OrderBy(x => x.Ticker)
                .ThenBy(x => x.Network)
                .ToList();
        }
        catch
        {
            return Array.Empty<ExchangeCurrency>();
        }
    }

    // =========================
    // Internal: Rate call
    // GET /retrieve-rate-value?fromCcy=BTC&toCcy=ETH&ccyAmount=1
    // =========================
    private async Task<RateDto?> GetRateAsync(string fromCcy, string toCcy, decimal amountFrom, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fromCcy) || string.IsNullOrWhiteSpace(toCcy) || amountFrom <= 0)
            return null;

        var qs =
            $"fromCcy={Uri.EscapeDataString(fromCcy)}&" +
            $"toCcy={Uri.EscapeDataString(toCcy)}&" +
            $"ccyAmount={Uri.EscapeDataString(amountFrom.ToString(CultureInfo.InvariantCulture))}";

        using var req = new HttpRequestMessage(HttpMethod.Get, $"retrieve-rate-value?{qs}");
        AddAuth(req);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        var res = await Http.SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
        if (res is null) return null;
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices) return null;

        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            var root = doc.RootElement;

            var result = root.TryGetProperty("result", out var rEl) && rEl.ValueKind == JsonValueKind.True;

            var rate = root.TryGetProperty("rate", out var rateEl) ? ReadDecimal(rateEl) : 0m;

            decimal? minFrom = null;
            decimal? maxFrom = null;

            if (root.TryGetProperty("minFrom", out var minEl))
                minFrom = ReadDecimalNullable(minEl);

            if (root.TryGetProperty("maxFrom", out var maxEl))
                maxFrom = ReadDecimalNullable(maxEl);

            return new RateDto
            {
                Result = result,
                Rate = rate,
                MinFrom = minFrom,
                MaxFrom = maxFrom,
            };
        }
        catch
        {
            return null;
        }
    }

    private void AddAuth(HttpRequestMessage req)
    {
        // Accept: (we know application/json caused 406 for you)
        req.Headers.Accept.Clear();
        req.Headers.TryAddWithoutValidation("Accept", "*/*");

        // API key (send both header spellings to be safe)
        req.Headers.Remove("x-api-key");
        req.Headers.Remove("X-API-KEY");
        req.Headers.TryAddWithoutValidation("x-api-key", opt.ApiKey);
        req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);

        // Remove Content-Type on GET (no body)
        //req.Headers.Remove("Content-Type");
    }

    // =========================
    // Key candidates (helps if your resolver didn’t set ExchangeId yet)
    // =========================
    private static IEnumerable<string> GetCodeCandidates(AssetRef asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId))
            yield return asset.ExchangeId.Trim();

        if (!string.IsNullOrWhiteSpace(asset.Ticker))
            yield return asset.Ticker.Trim().ToUpperInvariant();

        // Common special cases you keep hitting:
        if (asset.Ticker.Equals("USDT", StringComparison.OrdinalIgnoreCase) &&
            (asset.Network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            yield return "USDTTRC";
            yield return "USDTTRC20";
            yield return "USDTTRON";
            yield return "USDTTRX";
            yield return "USDT";
        }

        if (asset.Ticker.Equals("XMR", StringComparison.OrdinalIgnoreCase))
            yield return "XMR";
    }

    private static IEnumerable<string> PreferExchangeIdFirst(IEnumerable<string> candidates, string? exchangeId)
    {
        var list = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(exchangeId))
        {
            list.RemoveAll(x => x.Equals(exchangeId, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, exchangeId);
        }

        return list;
    }

    // =========================
    // Network normalization
    // =========================
    private static string NormalizeNetwork(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return "Mainnet";

        var n = network.Trim().ToLowerInvariant();

        return n switch
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
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n)
        };
    }

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
        return d <= 0 ? (decimal?)null : d;
    }

    private sealed class RateDto
    {
        public bool Result { get; set; }
        public decimal Rate { get; set; }
        public decimal? MinFrom { get; set; }
        public decimal? MaxFrom { get; set; }
    }
}