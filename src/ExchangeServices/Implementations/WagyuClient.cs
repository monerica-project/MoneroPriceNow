using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Xml.Linq;

namespace ExchangeServices.Implementations;

/// <summary>
/// Wagyu exchange client.
///
/// SELL (XMR → USDT): uses rates.xml  — XMR→USDTARBITRUM row; <out> is USDT per 1 XMR
///   (XMR rows use a tiny sentinel for <in>; we take <out> directly per IsXmrSentinel)
///
/// BUY  (USDT → XMR): uses POST /v1/quote
///   fromChainId=42161 (Arbitrum), fromToken=USDT, toToken=XMR
///   fromAmount = probe amount in smallest units (6 decimals for USDT)
///   buyPrice = probeUsdt / (toAmount / 1e12)   [XMR uses 12 decimal places]
/// </summary>
public sealed class WagyuClient : IWagyuClient
{
    private readonly HttpClient http;
    private readonly WagyuOptions opt;

    public string ExchangeKey => "wagyu";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    // In-instance cache for rates.xml (max-age=30s per Wagyu)
    private readonly object cacheLock = new();
    private DateTimeOffset cacheAtUtc;
    private List<RateItem>? cachedRates;

    public WagyuClient(HttpClient http, IOptions<WagyuOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ── Currencies ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var items = await GetRatesAsync(ct);
        if (items.Count == 0) return Array.Empty<ExchangeCurrency>();

        var symbols = items
            .SelectMany(i => new[] { i.From, i.To })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var list = new List<ExchangeCurrency>(symbols.Count);
        foreach (var sym in symbols)
        {
            var (ticker, network) = ParseSymbolToTickerNetwork(sym);
            if (string.IsNullOrWhiteSpace(ticker)) continue;
            list.Add(new ExchangeCurrency(
                ExchangeId: sym.Trim().ToLowerInvariant(),
                Ticker: ticker.ToUpperInvariant(),
                Network: network
            ));
        }

        return list.OrderBy(x => x.Ticker).ThenBy(x => x.Network).ToList();
    }

    // ── SELL: XMR → USDT via rates.xml ───────────────────────────────────────

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
        => await GetPriceAsync(query, ct);

    public async Task<PriceResult?> GetPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var items = await GetRatesAsync(ct);
        if (items.Count == 0) return null;

        var symbols = BuildSymbolSet(items);
        var fromSym = ResolveWagyuSymbol(query.Base, symbols);
        var toSym = ResolveWagyuSymbol(query.Quote, symbols);
        if (string.IsNullOrWhiteSpace(fromSym) || string.IsNullOrWhiteSpace(toSym)) return null;

        var best = TryGetBestSellRateDirected(items, fromSym, toSym, symbols);
        if (best is null || best.Value <= 0m) return null;

        return MakeResult(query, best.Value);
    }

    // ── BUY: USDT → XMR via rates.xml ────────────────────────────────────────
    // rates.xml has multiple USDTARBITRUM→XMR rows simulated at different probe
    // amounts (e.g. 25, 100, 400 USDT). Smaller probe amounts have worse effective
    // rates due to fixed fees — the row with the LARGEST <in> amount is the most
    // accurate proxy for "cost to buy 1 XMR" since it's closest to 1 XMR worth.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var items = await GetRatesAsync(ct);
        if (items.Count == 0) return null;

        // Find the USDT→XMR row with the largest <in> amount
        RateItem? bestRow = null;
        foreach (var it in items)
        {
            if (!it.To.Equals("XMR", StringComparison.OrdinalIgnoreCase)) continue;
            if (!it.From.StartsWith("USDT", StringComparison.OrdinalIgnoreCase)) continue;
            if (it.InAmount <= 0m || it.OutAmount <= 0m) continue;

            if (bestRow is null || it.InAmount > bestRow.InAmount)
                bestRow = it;
        }

        if (bestRow is null) return null;

        // out/in = XMR per USDT; invert = USDT per 1 XMR (buy price)
        var xmrPerUsdt = bestRow.OutAmount / bestRow.InAmount;
        if (xmrPerUsdt <= 0m) return null;

        var buyPrice = 1m / xmrPerUsdt;
        if (buyPrice <= 0m) return null;

        return MakeResult(query, buyPrice);
    }

    // ── rates.xml fetch + parsing ─────────────────────────────────────────────

    private async Task<List<RateItem>> GetRatesAsync(CancellationToken ct)
    {
        lock (cacheLock)
        {
            if (cachedRates is not null &&
                (DateTimeOffset.UtcNow - cacheAtUtc).TotalSeconds < Math.Max(1, opt.RatesCacheSeconds))
                return cachedRates;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        var retryCount = Math.Clamp(opt.RetryCount, 0, 6);

        var (status, body) = await SendWithRetryAsync(
            () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "/rates.xml");
                req.Headers.TryAddWithoutValidation("Accept", "application/xml");
                if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
                    req.Headers.UserAgent.ParseAdd(opt.UserAgent);
                return req;
            },
            timeout, retryCount, ct);

        if (status is null || string.IsNullOrWhiteSpace(body) ||
            (int)status < 200 || (int)status >= 300)
            return new List<RateItem>();

        List<RateItem> parsed;
        try { parsed = ParseRatesXml(body); }
        catch { parsed = new List<RateItem>(); }

        lock (cacheLock) { cachedRates = parsed; cacheAtUtc = DateTimeOffset.UtcNow; }
        return parsed;
    }

    private static List<RateItem> ParseRatesXml(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var root = doc.Root;
        if (root is null) return new List<RateItem>();

        var list = new List<RateItem>();
        foreach (var item in root.Elements("item"))
        {
            var from = (string?)item.Element("from") ?? "";
            var to = (string?)item.Element("to") ?? "";
            var inStr = (string?)item.Element("in") ?? "";
            var outStr = (string?)item.Element("out") ?? "";
            if (!TryDec(inStr, out var inAmt)) continue;
            if (!TryDec(outStr, out var outAmt)) continue;
            list.Add(new RateItem(from.Trim(), to.Trim(), inAmt, outAmt));
        }

        return list;

        static bool TryDec(string s, out decimal v) =>
            decimal.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private sealed record RateItem(string From, string To, decimal InAmount, decimal OutAmount);

    // ── Sell-side rate helpers ────────────────────────────────────────────────

    private static HashSet<string> BuildSymbolSet(List<RateItem> items) =>
        items
            .SelectMany(i => new[] { i.From, i.To })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static decimal? TryGetBestSellRateDirected(
        List<RateItem> items, string fromSym, string toSym, HashSet<string> symbols)
    {
        fromSym = (fromSym ?? "").Trim().ToUpperInvariant();
        toSym = (toSym ?? "").Trim().ToUpperInvariant();
        if (fromSym.Length == 0 || toSym.Length == 0) return null;
        if (fromSym.Equals(toSym, StringComparison.OrdinalIgnoreCase)) return 1m;

        if (TryGetDirectedEdgeRate(items, fromSym, toSym, out var direct)) return direct;

        decimal best = 0m;
        foreach (var mid in symbols)
        {
            if (mid.Equals(fromSym, StringComparison.OrdinalIgnoreCase) ||
                mid.Equals(toSym, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryGetDirectedEdgeRate(items, fromSym, mid, out var r1)) continue;
            if (!TryGetDirectedEdgeRate(items, mid, toSym, out var r2)) continue;
            var r = r1 * r2;
            if (r > best) best = r;
        }

        return best > 0m ? best : null;
    }

    private static bool TryGetDirectedEdgeRate(
        List<RateItem> items, string from, string to, out decimal rateToPerFrom)
    {
        rateToPerFrom = 0m;
        decimal best = 0m;

        foreach (var it in items)
        {
            if (!it.From.Equals(from, StringComparison.OrdinalIgnoreCase)) continue;
            if (!it.To.Equals(to, StringComparison.OrdinalIgnoreCase)) continue;
            if (it.OutAmount <= 0m) continue;

            decimal r;
            // XMR→* rows: <in> is a tiny sentinel; <out> is the per-1-XMR quote directly
            if (IsXmrSentinel(it.From, it.InAmount))
                r = it.OutAmount;
            else
            {
                if (it.InAmount <= 0m) continue;
                r = it.OutAmount / it.InAmount;
            }

            if (r > best) best = r;
        }

        if (best <= 0m) return false;
        rateToPerFrom = best;
        return true;
    }

    private static bool IsXmrSentinel(string fromSym, decimal inAmount) =>
        fromSym.Equals("XMR", StringComparison.OrdinalIgnoreCase) && inAmount > 0m && inAmount < 0.000001m;

    // ── Symbol resolution ─────────────────────────────────────────────────────

    private static string ResolveWagyuSymbol(AssetRef asset, HashSet<string> symbols)
    {
        var ex = (asset.ExchangeId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ex))
        {
            var exUpper = ex.ToUpperInvariant();
            if (exUpper.Contains('|'))
            {
                var parts = exUpper.Split('|', 2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                    exUpper = parts[0].Trim().ToUpperInvariant() + NormalizeNetworkSuffix(parts[1]);
            }
            if (symbols.Contains(exUpper)) return exUpper;
        }

        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ticker)) return "";

        if (ticker.Equals("XMR", StringComparison.OrdinalIgnoreCase)) return "XMR";

        var candidate = ticker + NormalizeNetworkSuffix(asset.Network);
        if (symbols.Contains(candidate)) return candidate;

        var preferredSuffixes = new[] { "ARBITRUM", "ETHEREUM", "BASE", "SOLANA", "BSC" };
        foreach (var suf in preferredSuffixes)
        {
            var alt = ticker + suf;
            if (symbols.Contains(alt)) return alt;
        }

        foreach (var s in symbols)
        {
            if (s.StartsWith(ticker, StringComparison.OrdinalIgnoreCase) &&
                !s.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return candidate;
    }

    private static string NormalizeNetworkSuffix(string? network) =>
        network?.Trim() switch
        {
            "Arbitrum" => "ARBITRUM",
            "Solana" => "SOLANA",
            "Ethereum" => "ETHEREUM",
            "Base" => "BASE",
            "BSC" => "BSC",
            "Binance Smart Chain" => "BSC",
            null or "" => "",
            var n => n.ToUpperInvariant()
        };

    private static (string ticker, string network) ParseSymbolToTickerNetwork(string symbol)
    {
        var s = (symbol ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s)) return ("", "");
        if (s.Equals("XMR", StringComparison.OrdinalIgnoreCase)) return ("XMR", "Mainnet");

        var suffixes = new[] { "ARBITRUM", "SOLANA", "ETHEREUM", "BASE", "BSC" };
        foreach (var suf in suffixes)
        {
            if (s.EndsWith(suf, StringComparison.OrdinalIgnoreCase) && s.Length > suf.Length)
            {
                var ticker = s[..^suf.Length];
                var network = suf switch
                {
                    "ARBITRUM" => "Arbitrum",
                    "SOLANA" => "Solana",
                    "ETHEREUM" => "Ethereum",
                    "BASE" => "Base",
                    "BSC" => "Binance Smart Chain",
                    _ => suf
                };
                return (ticker, network);
            }
        }

        return (s, "");
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<(HttpStatusCode? Status, string? Body)> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, TimeSpan timeout, int retryCount, CancellationToken ct)
    {
        var attempts = Math.Max(1, retryCount + 1);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = requestFactory();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            HttpResponseMessage? resp = null;
            try
            {
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (ShouldRetry(resp.StatusCode) && attempt < attempts - 1)
                { resp.Dispose(); await BackoffAsync(attempt, ct); continue; }
                return (resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                resp?.Dispose();
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return (null, null);
            }
            catch (HttpRequestException)
            {
                resp?.Dispose();
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return (null, null);
            }
            finally { resp?.Dispose(); }
        }
        return (null, null);
    }

    private static bool ShouldRetry(HttpStatusCode code)
    {
        var n = (int)code;
        return code == HttpStatusCode.RequestTimeout || n == 429 || (n >= 500 && n <= 599);
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = Math.Min(2000, (int)(200 * Math.Pow(2, attempt))) + Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}