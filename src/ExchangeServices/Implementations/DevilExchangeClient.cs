using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class DevilExchangeClient : IDevilExchangeClient
{
    private readonly HttpClient http;
    private readonly DevilExchangeOptions opt;

    /// <summary>Stores the last raw HTTP response from /api/v1/quote for debugging. Thread-safe via volatile.</summary>
    public volatile string? LastQuoteDebug;

    public string ExchangeKey => "devilexchange";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    // /pairs cache
    private readonly object pairsLock = new();
    private DateTimeOffset pairsAtUtc;
    private PairsSnapshot? cachedPairs;

    // short-lived quote cache (unit rates)
    private readonly object quoteLock = new();
    private readonly Dictionary<string, (DateTimeOffset atUtc, decimal rate)> quoteCache
        = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public DevilExchangeClient(HttpClient http, IOptions<DevilExchangeOptions> options)
    {
        this.http = http;
        this.opt = options.Value ?? new DevilExchangeOptions();

        if (this.http.BaseAddress is null && !string.IsNullOrWhiteSpace(this.opt.BaseUrl))
            this.http.BaseAddress = new Uri(this.opt.BaseUrl!, UriKind.Absolute);
    }

    // -------------------------
    // Public API
    // -------------------------

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var snap = await GetPairsSnapshotAsync(ct);
        if (snap.Symbols.Count == 0) return Array.Empty<ExchangeCurrency>();

        return snap.Symbols
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(sym => new ExchangeCurrency(
                ExchangeId: sym.Trim().ToLowerInvariant(),
                Ticker: sym.Trim().ToUpperInvariant(),
                Network: "" // Devil.Exchange public API does not expose networks
            ))
            .ToList();
    }

    // SELL alias
    public Task<PriceResult?> GetPriceAsync(PriceQuery query, CancellationToken ct = default)
        => GetSellPriceAsync(query, ct);

    // SELL = QUOTE received for 1 BASE
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var snap = await GetPairsSnapshotAsync(ct);
        if (snap.Pairs.Count == 0) return null;

        var fromCands = GetSymbolCandidates(query.Base, snap.Symbols);
        var toCands = GetSymbolCandidates(query.Quote, snap.Symbols);
        if (fromCands.Count == 0 || toCands.Count == 0) return null;

        var rateType = NormalizeRateType(opt.RateType);

        // Try every (from, to) candidate combination — most likely hit first
        foreach (var from in fromCands)
            foreach (var to in toCands)
            {
                var unit = await GetUnitRateAsync(from, to, rateType, snap, ct);
                if (unit is not null && unit.Value > 0m)
                    return new PriceResult(
                        Exchange: ExchangeKey,
                        Base: query.Base,
                        Quote: query.Quote,
                        Price: unit.Value,
                        TimestampUtc: DateTimeOffset.UtcNow,
                        CorrelationId: null,
                        Raw: null
                    );
            }

        return null;
    }

    // BUY = QUOTE required to get exactly 1 BASE
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var snap = await GetPairsSnapshotAsync(ct);
        if (snap.Pairs.Count == 0) return null;

        var baseCands = GetSymbolCandidates(query.Base, snap.Symbols);
        var quoteCands = GetSymbolCandidates(query.Quote, snap.Symbols);
        if (baseCands.Count == 0 || quoteCands.Count == 0) return null;

        var rateType = NormalizeRateType(opt.RateType);

        // ── Primary strategy: amount_side=to ────────────────────────────────
        // Ask "I want to RECEIVE exactly 1 BASE (XMR). How much QUOTE (USDT) must I send?"
        // The API returns amount_from = the exact USDT cost — no inversion, no rounding error.
        //
        //   GET /api/v1/quote?from=USDTTRC20&to=XMR&amount=1&amount_side=to
        //   → { "quote": { "amount_to_receive": 1, "amount_to_send": 365.95, "rate": ... } }
        //                                                         ^^^^^^ this IS the buy price
        //
        // Devil also supports amount_to param directly, but amount_side=to with amount=1
        // is the cleanest single-param override.
        foreach (var baseSym in baseCands)
            foreach (var quoteSym in quoteCands)
            {
                var cost = await GetBuyPriceDirectAsync(quoteSym, baseSym, rateType, snap, ct);
                if (cost is not null && cost.Value > 0m)
                    return new PriceResult(
                        Exchange: ExchangeKey,
                        Base: query.Base,
                        Quote: query.Quote,
                        Price: cost.Value,
                        TimestampUtc: DateTimeOffset.UtcNow,
                        CorrelationId: null,
                        Raw: null
                    );
            }

        // ── Fallback: invert the QUOTE→BASE unit rate ────────────────────────
        // Only reached if amount_side=to probe fails (e.g. pair not supported that way).
        foreach (var baseSym in baseCands)
            foreach (var quoteSym in quoteCands)
            {
                var basePerQuote = await GetUnitRateAsync(quoteSym, baseSym, rateType, snap, ct);
                if (basePerQuote is not null && basePerQuote.Value > 0m)
                {
                    var cost = 1m / basePerQuote.Value;
                    if (cost > 0m)
                        return new PriceResult(
                            Exchange: ExchangeKey,
                            Base: query.Base,
                            Quote: query.Quote,
                            Price: cost,
                            TimestampUtc: DateTimeOffset.UtcNow,
                            CorrelationId: null,
                            Raw: null
                        );
                }
            }

        return null;
    }

    /// <summary>
    /// Asks the API: "I want to receive exactly 1 <paramref name="to"/> (e.g. XMR).
    /// How much <paramref name="from"/> (e.g. USDT) must I send?"
    ///
    /// Uses amount_side=to so the API interprets amount=1 as the TO side.
    /// Reads AmountFrom from the response — that is the buy price with no inversion.
    /// Falls back to amount_to= explicit param if amount_side is not honoured.
    /// </summary>
    private async Task<decimal?> GetBuyPriceDirectAsync(
        string from, string to, string rateType, PairsSnapshot snap, CancellationToken ct)
    {
        // Probe: receive exactly 1 unit of `to`
        var q = await GetQuoteAsync(from, to, amount: 1m, rateType: rateType,
                                    amountSide: "to", ct: ct);

        if (q is not null && q.Success && q.AmountFrom is not null && q.AmountFrom.Value > 0m)
            return q.AmountFrom.Value;

        // amount_out_of_range on the TO side — try the explicit amount_to= param
        if (q is not null && q.OutOfRange && q.MinAmount is not null)
        {
            // min here is in the TO currency (XMR); pick a valid TO amount then normalise back
            var toAmt = ChooseAmountInRange(1m, q.MinAmount, q.MaxAmount);
            var q2 = await GetQuoteAsync(from, to, amount: toAmt, rateType: rateType,
                                         amountSide: "to", ct: ct);

            if (q2 is not null && q2.Success &&
                q2.AmountFrom is not null && q2.AmountFrom.Value > 0m &&
                q2.AmountTo is not null && q2.AmountTo.Value > 0m)
            {
                // normalise back to per-1-unit-of-to
                return q2.AmountFrom.Value / q2.AmountTo.Value;
            }
        }

        return null;
    }

    // -------------------------
    // Core: pairs + quoting
    // -------------------------

    private async Task<PairsSnapshot> GetPairsSnapshotAsync(CancellationToken ct)
    {
        lock (pairsLock)
        {
            if (cachedPairs is not null &&
                (DateTimeOffset.UtcNow - pairsAtUtc).TotalSeconds < Math.Max(10, opt.PairsCacheSeconds))
            {
                return cachedPairs;
            }
        }

        // GET /api/v1/pairs
        // Documented optional params: target=SYMBOL, direction=to|from|both
        // We request direction=both for full pair coverage in one call.
        var (_, bodyBoth) = await SendForStringWithRetryAsync(
            requestFactory: () => BuildRequest("/api/v1/pairs?direction=both"),
            timeout: TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60)),
            retryCount: Math.Clamp(opt.RetryCount, 0, 6),
            ct: ct);

        var snap = ParsePairs(bodyBoth);

        // Fallback: plain /pairs if direction=both is not supported or returned nothing
        if (snap.Pairs.Count == 0)
        {
            var (_, bodyDefault) = await SendForStringWithRetryAsync(
                requestFactory: () => BuildRequest("/api/v1/pairs"),
                timeout: TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60)),
                retryCount: Math.Clamp(opt.RetryCount, 0, 6),
                ct: ct);

            snap = ParsePairs(bodyDefault);
        }

        lock (pairsLock)
        {
            cachedPairs = snap;
            pairsAtUtc = DateTimeOffset.UtcNow;
        }

        return snap;
    }

    /// <summary>
    /// Calls GET /api/v1/quote per the Devil.Exchange API spec.
    ///
    /// Required params : from, to, amount
    /// Optional params : rate_type (floating|fixed), amount_side (from|to),
    ///                   amount_from, amount_to
    ///
    /// Default semantics (per docs):
    ///   floating → amount = from side (you send)
    ///   fixed    → amount = to   side (you receive)
    ///
    /// We always pass amount_side=from so the amount is always interpreted as
    /// "what you send", giving us a consistent unit-rate regardless of rate_type.
    /// </summary>
    private async Task<QuoteApiResult?> GetQuoteAsync(
        string from,
        string to,
        decimal amount,
        string rateType,
        string? amountSide = null,
        CancellationToken ct = default)
    {
        var qs = new List<string>
        {
            $"from={Uri.EscapeDataString(from)}",
            $"to={Uri.EscapeDataString(to)}",
            $"amount={Uri.EscapeDataString(amount.ToString(CultureInfo.InvariantCulture))}",
            $"rate_type={Uri.EscapeDataString(rateType)}"
        };

        // Override fixed-mode default (amount=to) so amount always means "from"
        if (!string.IsNullOrWhiteSpace(amountSide))
            qs.Add($"amount_side={Uri.EscapeDataString(amountSide)}");
        else if (rateType == "fixed")
            qs.Add("amount_side=from");

        var url = "/api/v1/quote?" + string.Join("&", qs);

        var (_, body) = await SendForStringWithRetryAsync(
            requestFactory: () => BuildRequest(url),
            timeout: TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60)),
            retryCount: Math.Clamp(opt.RetryCount, 0, 6),
            ct: ct);

        if (string.IsNullOrWhiteSpace(body))
        {
            LastQuoteDebug = $"[EMPTY BODY] url={url}";
            return null;
        }
        LastQuoteDebug = body;
        return ParseQuote(body);
    }

    /// <summary>
    /// Returns QUOTE per 1 FROM (unit rate).
    /// Clamps into min/max if 1 is out of range, normalises back to per-unit.
    /// Falls back through common intermediaries on no_route.
    /// </summary>
    private async Task<decimal?> GetUnitRateAsync(
        string from, string to, string rateType, PairsSnapshot snap, CancellationToken ct)
    {
        from = (from ?? "").Trim().ToUpperInvariant();
        to = (to ?? "").Trim().ToUpperInvariant();
        if (from.Length == 0 || to.Length == 0) return null;
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) return 1m;

        var cacheKey = $"{rateType}|{from}->{to}";
        lock (quoteLock)
        {
            if (quoteCache.TryGetValue(cacheKey, out var hit) &&
                (DateTimeOffset.UtcNow - hit.atUtc).TotalSeconds < Math.Max(1, opt.QuoteCacheSeconds))
            {
                return hit.rate;
            }
        }

        var direct = await GetUnitRateDirectAsync(from, to, rateType, snap, ct);
        if (direct is not null && direct.Value > 0m)
        {
            lock (quoteLock) quoteCache[cacheKey] = (DateTimeOffset.UtcNow, direct.Value);
            return direct.Value;
        }

        // One retry after a brief pause — transient failures are common
        await Task.Delay(300, ct).ConfigureAwait(false);
        direct = await GetUnitRateDirectAsync(from, to, rateType, snap, ct);
        if (direct is not null && direct.Value > 0m)
        {
            lock (quoteLock) quoteCache[cacheKey] = (DateTimeOffset.UtcNow, direct.Value);
            return direct.Value;
        }

        // 2-hop fallback — Devil routes everything through XMR, so this is only
        // useful when NEITHER side is XMR. If one side is already XMR, the direct
        // call is the only valid path; 2-hop would just waste API calls.
        var isXmrPair = from.Equals("XMR", StringComparison.OrdinalIgnoreCase) ||
                        to.Equals("XMR", StringComparison.OrdinalIgnoreCase);

        if (!isXmrPair)
        {
            var mids = new[] { "XMR", "USDT", "USDC", "BTC", "ETH", "LTC", "SOL" }
                .Where(m => snap.Symbols.Contains(m) &&
                            !m.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                            !m.Equals(to, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            decimal best = 0m;
            foreach (var mid in mids)
            {
                ct.ThrowIfCancellationRequested();

                var r1 = await GetUnitRateDirectAsync(from, mid, rateType, snap, ct);
                if (r1 is null || r1.Value <= 0m) continue;

                var r2 = await GetUnitRateDirectAsync(mid, to, rateType, snap, ct);
                if (r2 is null || r2.Value <= 0m) continue;

                var r = r1.Value * r2.Value;
                if (r > best) best = r;
            }

            if (best > 0m)
            {
                lock (quoteLock) quoteCache[cacheKey] = (DateTimeOffset.UtcNow, best);
                return best;
            }
        }

        return null;
    }

    private async Task<decimal?> GetUnitRateDirectAsync(
        string from, string to, string rateType, PairsSnapshot snap, CancellationToken ct)
    {
        snap.TryGetLimits(from, to, out var min, out var max);

        var amount = ChooseAmountInRange(desired: 1m, min: min, max: max);

        var q = await GetQuoteAsync(from, to, amount, rateType, ct: ct);
        if (q is null)
        {
            LastQuoteDebug = (LastQuoteDebug ?? "null") + $" | GetUnitRateDirectAsync: q=null for {from}->{to} amount={amount}";
            return null;
        }

        if (!q.Success)
        {
            // API returned { error:"amount_out_of_range", limits:{ min_amount, max_amount } }
            if (q.OutOfRange && q.MinAmount is not null)
            {
                var amt2 = ChooseAmountInRange(desired: 1m, min: q.MinAmount, max: q.MaxAmount);
                if (amt2 != amount)
                {
                    var q2 = await GetQuoteAsync(from, to, amt2, rateType, ct: ct);
                    if (q2 is null || !q2.Success) return null;

                    // Same convention as above — derive from amounts, not q.Rate
                    var r2 =
                        (q2.AmountTo is not null && amt2 > 0m)
                            ? q2.AmountTo.Value / amt2
                            : q2.Rate;
                    return r2 is not null && r2.Value > 0m ? r2.Value : null;
                }
            }

            // no_route or other — signal caller to try fallback
            return null;
        }

        // IMPORTANT: Do NOT use q.Rate directly.
        // Devil.Exchange returns `quote.rate` in a fixed convention (to/from for their
        // canonical direction, e.g. always ~340 for any XMR/USDT pair regardless of which
        // side is `from`). For a USDT→XMR probe this gives 340 instead of 0.00294, which
        // makes GetBuyPriceAsync invert it to 0.003 instead of ~340 — completely wrong.
        //
        // Always derive the unit rate from the actual amounts in the response:
        //   rate = amount_to_receive / amount_sent
        // This is directionally correct for both XMR→USDT and USDT→XMR.
        // Fall back to q.Rate only if we have no amounts at all.
        var rate =
            (q.AmountTo is not null && amount > 0m)
                ? q.AmountTo.Value / amount
                : q.Rate;

        return rate is not null && rate.Value > 0m ? rate.Value : null;
    }

    private static decimal ChooseAmountInRange(decimal desired, decimal? min, decimal? max)
    {
        var amt = desired;

        if (min is not null && min.Value > 0m && amt < min.Value)
            amt = min.Value * 1.05m;   // slight bump above min

        if (max is not null && max.Value > 0m && amt > max.Value)
            amt = max.Value * 0.95m;   // slight margin below max

        if (amt <= 0m) amt = desired > 0m ? desired : 1m;

        return decimal.Round(amt, 8);
    }

    // -------------------------
    // Parsing
    // -------------------------

    private sealed class PairsSnapshot
    {
        public HashSet<string> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<PairInfo> Pairs { get; } = new();

        public bool TryGetLimits(string from, string to, out decimal? min, out decimal? max)
        {
            min = null; max = null;
            var p = Pairs.FirstOrDefault(x =>
                x.From.Equals(from, StringComparison.OrdinalIgnoreCase) &&
                x.To.Equals(to, StringComparison.OrdinalIgnoreCase));

            if (p is null) return false;
            min = p.MinAmount;
            max = p.MaxAmount;
            return true;
        }
    }

    private sealed record PairInfo(
        string From,
        string To,
        decimal? MinAmount,
        decimal? MaxAmount,
        bool SupportsFloating,
        bool SupportsFixed);

    /// <summary>
    /// Parses /api/v1/pairs.
    /// Devil.Exchange documented shape: { success, timestamp, pairs:[{ from, to, ... }] }
    /// Also tolerates bare arrays and data:[...] wrappers.
    /// </summary>
    private static PairsSnapshot ParsePairs(string? json)
    {
        var snap = new PairsSnapshot();
        if (string.IsNullOrWhiteSpace(json)) return snap;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
            {
                arr = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetArray(root, "pairs", out var pairsArr))
            {
                arr = pairsArr;
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetArray(root, "data", out var dataArr))
            {
                arr = dataArr;
            }
            else
            {
                return snap;
            }

            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                var from = GetString(el, "from") ?? GetString(el, "coinFrom") ?? GetString(el, "source") ?? "";
                var to = GetString(el, "to") ?? GetString(el, "coinTo") ?? GetString(el, "destination") ?? "";
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) continue;

                from = from.Trim().ToUpperInvariant();
                to = to.Trim().ToUpperInvariant();

                var min = GetDecimal(el, "min_amount") ?? GetDecimal(el, "minAmount");
                var max = GetDecimal(el, "max_amount") ?? GetDecimal(el, "maxAmount");

                var supportsFloating = true;
                var supportsFixed = true;

                if (TryGetArray(el, "rate_types", out var rtArr) || TryGetArray(el, "rateTypes", out rtArr))
                {
                    supportsFloating = false;
                    supportsFixed = false;
                    foreach (var rt in rtArr.EnumerateArray())
                    {
                        var s = rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
                        if (s is null) continue;
                        if (s.Equals("floating", StringComparison.OrdinalIgnoreCase)) supportsFloating = true;
                        if (s.Equals("fixed", StringComparison.OrdinalIgnoreCase)) supportsFixed = true;
                    }
                }
                else if (el.TryGetProperty("supports", out var sup) && sup.ValueKind == JsonValueKind.Object)
                {
                    var f = GetBool(sup, "floating");
                    var fx = GetBool(sup, "fixed");
                    if (f is not null) supportsFloating = f.Value;
                    if (fx is not null) supportsFixed = fx.Value;
                }

                snap.Pairs.Add(new PairInfo(from, to, min, max, supportsFloating, supportsFixed));
                snap.Symbols.Add(from);
                snap.Symbols.Add(to);
            }
        }
        catch
        {
            // return partial/empty snapshot on any parse error
        }

        return snap;
    }

    private sealed class QuoteApiResult
    {
        public bool Success { get; init; }
        public bool OutOfRange { get; init; }
        public string? Error { get; init; }
        public string? Message { get; init; }
        public string? Timestamp { get; init; }

        // Devil.Exchange quote response fields
        public decimal? AmountFrom { get; init; }  // amount_from — what you send
        public decimal? AmountTo { get; init; }  // amount_to   — what you receive
        public decimal? Rate { get; init; }  // exchange rate

        // Error limits (from { limits:{ min_amount, max_amount } })
        public decimal? MinAmount { get; init; }
        public decimal? MaxAmount { get; init; }
    }

    /// <summary>
    /// Parses /api/v1/quote response.
    ///
    /// Actual Devil.Exchange success shape (confirmed from live response):
    /// {
    ///   "success": true,
    ///   "provider": "devil.exchange",
    ///   "pair": {
    ///     "from": "XMR", "to": "USDTTRC20",
    ///     "network_from": "mainnet", "network_to": "trx",
    ///     "rate_type": "floating",
    ///     "limits": { "min_amount": 0.00377, "max_amount": 300, "unit": "XMR" },
    ///     "input":  { "amount_from": 1, "side": "from", "amount_to_receive": 340.675, "amount_to": 340.675 },
    ///     "quote":  { "rate": 340.675, "amount_to_send": 1, "amount_to_receive": 340.675,
    ///                 "fee_fraction": 0.005, "fee_percent_display": 0.5 },
    ///     "fixed":  { "rate_id": null, "valid_until": null }
    ///   },
    ///   "timestamp": "2026-03-01T14:43:20+00:00"
    /// }
    ///
    /// Error shapes:
    ///   { success:false, error:"amount_out_of_range", message:"...", limits:{ min_amount, max_amount } }
    ///   { success:false, error:"no_route", message:"..." }
    /// </summary>
    private static QuoteApiResult? ParseQuote(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var success = GetBool(root, "success") ?? true;
            var error = GetString(root, "error") ?? GetString(root, "code");
            var message = GetString(root, "message") ?? GetString(root, "detail");
            var timestamp = GetString(root, "timestamp");

            decimal? amountFrom = null;
            decimal? amountTo = null;
            decimal? rate = null;
            decimal? min = null;
            decimal? max = null;

            // ── Primary: Devil.Exchange v2 response has quote/input/limits at ROOT level ──
            // The "pair" object only contains from/to/network_from/network_to.
            // We check both root level and inside pair for forward compatibility.

            // Look for "quote" block — at root first, then inside pair
            JsonElement quoteEl = default;
            bool hasQuote = (root.TryGetProperty("quote", out quoteEl) && quoteEl.ValueKind == JsonValueKind.Object);
            if (!hasQuote && root.TryGetProperty("pair", out var pairEl) && pairEl.ValueKind == JsonValueKind.Object)
                hasQuote = pairEl.TryGetProperty("quote", out quoteEl) && quoteEl.ValueKind == JsonValueKind.Object;

            if (hasQuote)
            {
                rate =
                    GetDecimal(quoteEl, "rate") ??
                    GetDecimal(quoteEl, "exchange_rate") ??
                    GetDecimal(quoteEl, "exchangeRate");

                amountTo =
                    GetDecimal(quoteEl, "amount_to_receive") ??
                    GetDecimal(quoteEl, "amount_to") ??
                    GetDecimal(quoteEl, "amountTo");

                amountFrom =
                    GetDecimal(quoteEl, "amount_to_send") ??
                    GetDecimal(quoteEl, "amount_from") ??
                    GetDecimal(quoteEl, "amountFrom");
            }

            // Look for "input" block — at root first, then inside pair
            JsonElement inputEl = default;
            bool hasInput = (root.TryGetProperty("input", out inputEl) && inputEl.ValueKind == JsonValueKind.Object);
            if (!hasInput && root.TryGetProperty("pair", out var pairEl2) && pairEl2.ValueKind == JsonValueKind.Object)
                hasInput = pairEl2.TryGetProperty("input", out inputEl) && inputEl.ValueKind == JsonValueKind.Object;

            if (hasInput)
            {
                amountTo ??= GetDecimal(inputEl, "amount_to_receive") ?? GetDecimal(inputEl, "amount_to");
                amountFrom ??= GetDecimal(inputEl, "amount_from") ?? GetDecimal(inputEl, "amount_to_send");
            }

            // Look for "limits" block — at root first, then inside pair
            JsonElement limEl = default;
            bool hasLim = (root.TryGetProperty("limits", out limEl) && limEl.ValueKind == JsonValueKind.Object);
            if (!hasLim && root.TryGetProperty("pair", out var pairEl3) && pairEl3.ValueKind == JsonValueKind.Object)
                hasLim = pairEl3.TryGetProperty("limits", out limEl) && limEl.ValueKind == JsonValueKind.Object;

            if (hasLim)
            {
                min = GetDecimal(limEl, "min_amount") ?? GetDecimal(limEl, "minAmount") ?? GetDecimal(limEl, "min");
                max = GetDecimal(limEl, "max_amount") ?? GetDecimal(limEl, "maxAmount") ?? GetDecimal(limEl, "max");
            }

            // ── Fallback: flat root-level fields (other exchange shapes / error responses) ──
            rate ??= GetDecimal(root, "rate") ?? GetDecimal(root, "exchange_rate") ?? GetDecimal(root, "exchangeRate");
            amountTo ??= GetDecimal(root, "amount_to") ?? GetDecimal(root, "amountTo") ?? GetDecimal(root, "to_amount")
                         ?? GetDecimal(root, "toAmount") ?? GetDecimal(root, "amountOut") ?? GetDecimal(root, "result");
            amountFrom ??= GetDecimal(root, "amount_from") ?? GetDecimal(root, "amountFrom") ?? GetDecimal(root, "from_amount");

            // error-response limits: { limits:{ min_amount, max_amount } }
            if (min is null && root.TryGetProperty("limits", out var errLim) && errLim.ValueKind == JsonValueKind.Object)
            {
                min = GetDecimal(errLim, "min_amount") ?? GetDecimal(errLim, "minAmount");
                max = GetDecimal(errLim, "max_amount") ?? GetDecimal(errLim, "maxAmount");
            }

            min ??= GetDecimal(root, "min_amount") ?? GetDecimal(root, "minAmount");
            max ??= GetDecimal(root, "max_amount") ?? GetDecimal(root, "maxAmount");

            var outOfRange =
                !success &&
                (string.Equals(error, "amount_out_of_range", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(error, "OUT_OF_RANGE", StringComparison.OrdinalIgnoreCase));

            return new QuoteApiResult
            {
                Success = success,
                OutOfRange = outOfRange,
                Error = error,
                Message = message,
                Timestamp = timestamp,
                AmountFrom = amountFrom,
                AmountTo = amountTo,
                Rate = rate,
                MinAmount = min,
                MaxAmount = max
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns an ordered list of symbol candidates to try for this asset.
    ///
    /// Devil.Exchange uses compound symbols like USDTTRC20, USDTERC20, USDTBSC.
    /// The pairs API (direction=both) lists both X→XMR and XMR→X pairs, so
    /// network-specific symbols like USDTTRC20 ARE present in snap.Symbols.
    /// We always generate network-suffixed candidates first (most specific)
    /// then plain ticker last, because plain USDT is ambiguous when multiple
    /// networks exist.
    /// </summary>
    private static List<string> GetSymbolCandidates(AssetRef asset, HashSet<string> symbols)
    {
        var ordered = new List<string>();

        void Add(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(s)) ordered.Add(s);
        }

        var ex = (asset.ExchangeId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ex))
        {
            if (ex.Contains('|'))
            {
                var parts = ex.Split('|', 2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    var a = parts[0].Trim().ToUpperInvariant();
                    foreach (var suf in NetworkSuffixCandidates(parts[1])) Add(a + suf);
                    Add(a);
                }
            }
            Add(ex);
        }

        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(ticker))
        {
            // Network-specific variants first (e.g. USDTTRC20, USDTERC20)
            // Plain ticker last — it may be ambiguous on exchanges that have
            // multiple networks for the same coin.
            foreach (var suf in NetworkSuffixCandidates(asset.Network)) Add(ticker + suf);
            Add(ticker);
        }

        // Deduplicate while preserving order
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var c in ordered)
            if (seen.Add(c)) result.Add(c);

        return result;
    }

    private static IEnumerable<string> NetworkSuffixCandidates(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return Array.Empty<string>();

        // Order matters: most-specific / most-likely Devil symbol first.
        // Confirmed from live API: USDTTRC20, not USDTTRX or USDTTRC.
        return network.Trim() switch
        {
            "Tron" or "TRX" or "TRC20" => new[] { "TRC20", "TRC", "TRX" },
            "Ethereum" or "ETH" or "ERC20" => new[] { "ERC20", "ETH" },
            "Binance Smart Chain" or "BSC" or "BEP20" => new[] { "BEP20", "BSC" },
            "Solana" or "SOL" => new[] { "SOL" },
            "Arbitrum" or "ARB" => new[] { "ARB", "ARBITRUM" },
            "Base" => new[] { "BASE" },
            "Polygon" or "MATIC" => new[] { "MATIC", "POLYGON" },
            var n => new[] { SlugUpper(n) }
        };

        static string SlugUpper(string s) =>
            s.ToUpperInvariant()
             .Replace("(", "").Replace(")", "")
             .Replace("-", "").Replace("_", "")
             .Replace(" ", "");
    }

    private static string NormalizeRateType(string? rateType)
    {
        var r = (rateType ?? "").Trim();
        return r.Equals("fixed", StringComparison.OrdinalIgnoreCase) ? "fixed" : "floating";
    }

    // -------------------------
    // HTTP helpers
    // -------------------------

    private HttpRequestMessage BuildRequest(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
        return req;
    }

    private async Task<(HttpStatusCode? Status, string? Body)> SendForStringWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        int retryCount,
        CancellationToken ct)
    {
        var attempts = Math.Max(1, retryCount + 1);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = requestFactory();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                using var resp = await http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                var body = await resp.Content
                    .ReadAsStringAsync(cts.Token)
                    .ConfigureAwait(false);

                if (ShouldRetry(resp.StatusCode) && attempt < attempts - 1)
                {
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }

                return (resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < attempts - 1)
                {
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                return (null, null);
            }
            catch (HttpRequestException)
            {
                if (attempt < attempts - 1)
                {
                    await BackoffAsync(attempt, ct).ConfigureAwait(false);
                    continue;
                }
                return (null, null);
            }
        }

        return (null, null);
    }

    private static bool ShouldRetry(HttpStatusCode code)
    {
        var n = (int)code;
        if (code == HttpStatusCode.RequestTimeout) return true; // 408
        if (n == 429) return true;                              // Too Many Requests
        if (n >= 500 && n <= 599) return true;                  // 5xx
        return false;
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = Math.Min(2000, (int)(200 * Math.Pow(2, attempt)));
        ms += Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }

    // -------------------------
    // JSON helpers
    // -------------------------

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static bool? GetBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.True) return true;
        if (p.ValueKind == JsonValueKind.False) return false;
        if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
        return null;
    }

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;

        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : (decimal?)null,
            JsonValueKind.String => decimal.TryParse(
                p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null
        };
    }

    private static bool TryGetArray(JsonElement obj, string name, out JsonElement arr)
    {
        arr = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind != JsonValueKind.Array) return false;
        arr = p;
        return true;
    }
}