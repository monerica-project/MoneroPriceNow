using CryptoPriceNow.Data.Interfaces;
using CryptoPriceNow.Data.Models;
using CryptoPriceNow.Web.Models;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace CryptoPriceNow.Services;

public sealed class PriceService : IPriceService
{
    private readonly IReadOnlyList<IExchangePriceApi> priceApis;
    private readonly IReadOnlyList<IExchangeCurrencyApi> currencyApis;
    private readonly IMemoryCache cache;
    private readonly PriceServiceOptions opt;
    private readonly IPriceQuoteSink quoteSink;
    private readonly IHttpClientFactory httpFactory;

    // exchangeKey -> "float"|"fixed" — resolved once at startup from IRateType
    private readonly IReadOnlyDictionary<string, string> rateTypes;

    // ── Thundering-herd guard: one semaphore per per-exchange cache key ───────
    private readonly ConcurrentDictionary<string, SemaphoreSlim> keyLocks = new();

    // ── Assembled result store ────────────────────────────────────────────────
    // Keyed by "BASE->QUOTE" (e.g. "XMR->USDT:Tron").
    // Written atomically by RefreshAndStoreAsync; read by GetTwoWayPricesInternalAsync.
    // Requests always return the last good snapshot instantly; the background
    // warmer replaces it without ever evicting the old data first.
    private readonly ConcurrentDictionary<string, IReadOnlyList<TwoWayPriceRow>> latestRows = new();

    public PriceService(
        IEnumerable<IExchangePriceApi> priceApis,
        IEnumerable<IExchangeCurrencyApi> currencyApis,
        IMemoryCache cache,
        IOptions<PriceServiceOptions> options,
        IPriceQuoteSink quoteSink,
        IHttpClientFactory httpFactory)
    {
        this.opt = options.Value;
        this.httpFactory = httpFactory;

        // Drop exchanges that can't serve a two-way (buy + sell) XMR quote. This is
        // a two-way price site, so a sell-only venue (e.g. ChangeHero, which can't
        // receive/buy Monero) is excluded entirely rather than shown half-empty.
        var excluded = new HashSet<string>(
            this.opt.ExcludedExchanges ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        this.priceApis = priceApis.Where(a => !excluded.Contains(a.ExchangeKey)).ToList();

        this.currencyApis = currencyApis.ToList();
        this.cache = cache;
        this.quoteSink = quoteSink;

        this.rateTypes = this.priceApis.ToDictionary(
            a => a.ExchangeKey,
            a => (a as IRateType)?.RateType ?? RateTypes.Float,
            StringComparer.OrdinalIgnoreCase);
    }

    // ── Public helpers ────────────────────────────────────────────────────────

    public static AssetRef ParseAssetPublic(string s) => ParseAsset(s);

    // ── Called by PriceWarmingService ─────────────────────────────────────────
    // Fetches live data from every exchange API, then atomically stores the
    // assembled result. Does NOT evict existing cache entries first — the old
    // snapshot remains readable until the new one is ready.
    public async Task RefreshAndStoreAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct = default)
    {
        // Evict per-exchange cache entries so FetchLiveAsync calls the real APIs.
        // This is safe because latestRows still holds the previous snapshot —
        // any page load during this window returns stale-but-valid data instantly.
        foreach (var api in priceApis)
        {
            cache.Remove($"price:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}");
            foreach (var mode in new[] { "flt", "fix" })
            {
                cache.Remove($"sell:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}:{mode}");
                if (api is IExchangeBuyPriceApi)
                    cache.Remove($"buy:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}:{mode}");
            }
        }

        // Fetch float (all exchanges) + fixed (fixed-capable only) and combine, so the board can
        // toggle between them. Drop fixed rows that came back empty (the exchange didn't offer a
        // fixed rate for this pair) so the Fixed view lists only exchanges that actually quote fixed.
        var floatRows = await FetchLiveAsync(baseRef, quoteRef, fixedRate: false, ct);
        var floatByExchange = floatRows
            .GroupBy(r => r.Exchange, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Build the Fixed rows. A "fixed" quote within 0.1% of the exchange's float quote is not a
        // genuine fixed rate — the exchange just echoed its float rate for that side. Keep a row
        // only if the exchange offers a genuinely different fixed rate on at least ONE side; then
        // fill the OTHER side from its float quote so the row is complete. (Several exchanges only
        // quote fixed in one direction — e.g. XMR->BTC but not BTC->XMR — and a bare "--" there
        // looked broken.)
        var fixedRows = (await FetchLiveAsync(baseRef, quoteRef, fixedRate: true, ct))
            .Select(r =>
            {
                if (!floatByExchange.TryGetValue(r.Exchange, out var f))
                    return (row: r, genuine: r.Sell is not null || r.Buy is not null);

                var genuineSell = r.Sell is not null && !SameRate(r.Sell, f.Sell);
                var genuineBuy = r.Buy is not null && !SameRate(r.Buy, f.Buy);
                var row = r with
                {
                    Sell = genuineSell ? r.Sell : f.Sell,
                    Buy = genuineBuy ? r.Buy : f.Buy,
                };
                return (row, genuine: genuineSell || genuineBuy);
            })
            .Where(t => t.genuine)
            .Select(t => t.row);
        var rows = floatRows.Concat(fixedRows).ToList();

        // Resilience: if an exchange returned NOTHING this cycle (both sides null — e.g. a
        // Tor-routed source like Quickex whose call occasionally exceeds the per-exchange
        // timeout), carry forward its previous quote as long as it's still fresh. The old
        // TsUtc is kept, so the board honestly shows how stale that row is instead of flapping
        // to "--" on a single slow cycle.
        if (latestRows.TryGetValue(PairKey(baseRef, quoteRef), out var prevSnapshot) && prevSnapshot.Count > 0)
        {
            static string LastGoodKey(TwoWayPriceRow r) => $"{r.Exchange}|{r.RateType}".ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            var prevByKey = prevSnapshot
                .Where(r => r.Sell is not null || r.Buy is not null)
                .GroupBy(LastGoodKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            rows = rows
                .Select(r =>
                {
                    // Both sides landed this cycle → nothing to carry.
                    if (r.Sell is not null && r.Buy is not null) return r;

                    // No fresh last-good within the stale window → leave as-is (a one-sided row
                    // is still dropped by the both-sides read filter, exactly as before).
                    if (!prevByKey.TryGetValue(LastGoodKey(r), out var old)
                        || old.TsUtc is not DateTimeOffset ts
                        || now - ts >= LastGoodStaleWindow)
                        return r;

                    // Both sides null this cycle → carry the whole previous row (its old TsUtc is
                    // kept, so the board honestly shows staleness instead of flapping to "--").
                    if (r.Sell is null && r.Buy is null) return old;

                    // Only ONE side landed. This is the common failure for the slow
                    // curl-impersonate exchanges (e.g. xChange: buy and sell are two separate
                    // shell-outs, and one can exceed the per-exchange timeout on a given cycle).
                    // Fill just the MISSING side from the last-good quote so the row stays
                    // two-sided and doesn't flap off the board; drop TsUtc to the older
                    // contributing timestamp so staleness stays honest.
                    var filledSell = r.Sell ?? old.Sell;
                    var filledBuy = r.Buy ?? old.Buy;
                    if (filledSell is null || filledBuy is null) return r; // old also lacked it
                    var stamp = (r.TsUtc, old.TsUtc) switch
                    {
                        (DateTimeOffset a, DateTimeOffset b) => a < b ? a : b,
                        (DateTimeOffset a, _) => a,
                        (_, DateTimeOffset b) => b,
                        _ => r.TsUtc,
                    };
                    return r with { Sell = filledSell, Buy = filledBuy, TsUtc = stamp };
                })
                .ToList();
        }

        latestRows[PairKey(baseRef, quoteRef)] = rows;

        // Hand the snapshot to the quote logger. EnqueueAsync only writes to an
        // in-memory channel (or is a no-op when no DB is configured), so this
        // never slows the warm cycle or page loads.
        try
        {
            await quoteSink.EnqueueAsync(BuildSnapshot(baseRef, quoteRef, rows), ct);
        }
        catch
        {
            // Logging must never break price serving.
        }
    }

    // ── Keep ForceRefreshAllAsync for backward compat (warmer will switch) ────
    public Task ForceRefreshAllAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct = default)
        => RefreshAndStoreAsync(baseRef, quoteRef, ct);

    // ── Main query methods ────────────────────────────────────────────────────

    public Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesAsync(
        string @base, string quote, CancellationToken ct = default)
        => GetTwoWayPricesInternalAsync(ParseAsset(@base), ParseAsset(quote), ct);

    public Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct = default)
        => GetTwoWayPricesInternalAsync(baseRef, quoteRef, ct);

    private async Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesInternalAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        // Fast path: warmer has already built a snapshot. Cold start only (first request before
        // the warmer has finished its first run) fetches live float-only rows.
        var rows = latestRows.TryGetValue(PairKey(baseRef, quoteRef), out var cached)
            ? cached
            : await FetchLiveAsync(baseRef, quoteRef, fixedRate: false, ct);

        // Only surface exchanges quoting BOTH sides. A one-sided quote (buy XOR sell, incl. a
        // side nulled by Positive()) would skew the market mid toward whichever side is present
        // and clutter the comparison list, so the row is hidden until both sides come back. This
        // is applied at the single read path, so it holds EVERYWHERE the price is used — the
        // board, the mid/hero price, the /api and CDN feeds, and the per-exchange page. The raw
        // cache still keeps one-sided rows for carry-forward resilience and history logging.
        // Sanity-filter the spread so only rows that MAKE SENSE reach the board, the /api + CDN
        // feeds, and the per-exchange page (single read path, so it holds everywhere):
        //   • Buy < Sell  → a negative spread (you'd buy XMR cheaper than you could sell it).
        //     Stale/bad data, not a real arbitrage.
        //   • spread > MaxSpreadFraction → absurdly wide (real XMR spreads top out ~10-13%; a
        //     70%+ spread means one side, usually a mis-scaled buy quote, is garbage).
        // Either way the buy/sell pair is nonsensical, so the row is hidden rather than shown.
        var maxSpread = this.opt.MaxSpreadFraction;
        return rows
            .Where(r => IsSaneTwoWayRow(r.Buy, r.Sell, maxSpread))
            .ToList();
    }

    // A two-way quote is "sane" only when both sides are present, the spread is non-negative
    // (Buy >= Sell) and it isn't absurdly wide (> MaxSpreadFraction). Real XMR spreads top out
    // ~10-13%, so a 25%+ spread means one side — usually a mis-scaled quote returned by a broken
    // upstream endpoint (e.g. a buy figure ~500x too high) — is garbage.
    //
    // This is the SINGLE definition of "makes sense" and is applied at BOTH the read path (board,
    // /api + CDN feeds, per-exchange page) AND the history snapshot fed to the quote logger. Sharing
    // it means the charts can never diverge from the board: one exchange's insane quote is excluded
    // everywhere, so it can never corrupt the pooled PriceBuckets rollup the charts are built from.
    internal static bool IsSaneTwoWayRow(decimal? buy, decimal? sell, decimal maxSpread)
    {
        if (buy is not decimal b || sell is not decimal s) return false; // both sides required
        if (b < s) return false;                                         // negative spread → bad/stale
        if (maxSpread <= 0m || b <= 0m) return true;                     // guard disabled / no divisor
        return (b - s) / b <= maxSpread;                                 // reject absurdly-wide spreads
    }

    // ── Live fetch (calls exchange APIs in parallel, respects per-exchange TTL) 
    // Exchanges whose ExchangeServices client honours PriceQuery.Fixed (returns a genuine
    // fixed-rate quote). Only these are queried for the Fixed view — the rest would just echo
    // their float rate, which would be wrong to label "fixed".
    private static readonly HashSet<string> FixedCapableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "changenow", "fixedfloat", "exolix", "stealthex", "simpleswap", "trocador", "letsexchange",
        "0trace", "swapuz", "changee", "swapgate", "bitania", "quickex", "pegasusswap", "sageswap",
        "swapzone", // aggregator supports rateType=fixed (client maps query.Fixed → fixed)
        "elcapo",   // supports rate_type=fixed on /api/partner/rate (client maps query.Fixed → fixed)
        "explace",  // supports details.type=fix (client maps query.Fixed → fix)
        "ccecash",  // supports exchange_mode=fixed on /calculate (client maps query.Fixed → fixed)
        "etzswap",  // supports rateType=fixed on /deposit/public/rate (client maps query.Fixed → fixed)
        "alfacash", // rate.json returns rate (fixed) + rate_floating; client maps query.Fixed → fixed rate
        "flashift", // aggregator; getEstimatedAmount returns floating + fixed offers (client maps query.Fixed → fixed)
    };

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(8);

    // How long a previous quote may be carried forward when an exchange returns nothing on a
    // refresh cycle (see RefreshAndStoreAsync). Long enough to ride out an intermittently slow
    // source (Tor-routed Quickex times out ~20% of cycles), short enough that a truly-down
    // exchange still drops off before its price is meaningfully stale.
    private static readonly TimeSpan LastGoodStaleWindow = TimeSpan.FromMinutes(20);

    // A min-amount below ~$1 is not a real USD minimum: it's either 0 ("unknown") or a value the
    // client returned in the QUOTE currency (e.g. 0.0001147 BTC) instead of USD. Crypto-swap
    // minimums are always well above $1, so treat sub-$1 as unknown → the board shows "--" instead
    // of "$0.00" and sorts it as absent.
    private static decimal? Positive(decimal? v) => v is >= 1m ? v : null;

    // True when a "fixed" quote is effectively identical to the float quote (within 0.1%). A real
    // fixed rate carries a spread well above this, so a match means the exchange just echoed its
    // float rate for that side — we null it so the Fixed view never duplicates the Float board.
    private static bool SameRate(decimal? a, decimal? b)
        => a is decimal x && b is decimal y && y != 0m && Math.Abs(x - y) / Math.Abs(y) < 0.001m;
    private async Task<IReadOnlyList<TwoWayPriceRow>> FetchLiveAsync(
        AssetRef baseRef, AssetRef quoteRef, bool fixedRate, CancellationToken ct)
    {
        // Float pass = every exchange; Fixed pass = only the fixed-capable ones.
        var apis = fixedRate
            ? priceApis.Where(a => FixedCapableKeys.Contains(a.ExchangeKey))
            : (IEnumerable<IExchangePriceApi>)priceApis;

        // Size the $2,500-standardized probes ONCE per cycle, before the fan-out — so the USD
        // price lookup (rate-limited) is never inside an exchange's per-call timeout budget and
        // isn't serialized across every exchange. Every exchange in this cycle uses the same size.
        var sellProbe = await SellProbeAsync(baseRef, ct);
        var buyProbe = await BuyProbeAsync(quoteRef, ct);

        var tasks = apis.Select(async api =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ExchangeTimeout);

            var rateType = fixedRate
                ? RateTypes.Fixed
                : (rateTypes.TryGetValue(api.ExchangeKey, out var rt) ? rt : RateTypes.Float);

            try
            {
                var sellRes = await GetOneExchangeSellAsync(api, baseRef, quoteRef, sellProbe, fixedRate, cts.Token);
                var buyRes = api is IExchangeBuyPriceApi buyApi
                    ? await GetOneExchangeBuyAsync(api.ExchangeKey, buyApi, baseRef, quoteRef, buyProbe, fixedRate, cts.Token)
                    : null;

                var ts = sellRes?.TimestampUtc;
                if (buyRes is not null && (ts is null || buyRes.TimestampUtc > ts.Value))
                    ts = buyRes.TimestampUtc;

                return new TwoWayPriceRow(
                    Exchange: api.ExchangeKey,
                    SiteName: api.SiteName,
                    SiteUrl: api.SiteUrl,
                    Sell: sellRes?.Price,
                    Buy: buyRes?.Price,
                    TsUtc: ts,
                    PrivacyLevel: (api as IPrivacyLevel)?.PrivacyLevel,
                    MinAmountUsd: Positive(sellRes?.MinAmountUsd)
                                  ?? Positive(buyRes?.MinAmountUsd)
                                  ?? Positive((api as IMinAmountUsd)?.MinAmountUsd),
                    RateType: rateType
                );
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new TwoWayPriceRow(
                    Exchange: api.ExchangeKey,
                    SiteName: api.SiteName,
                    SiteUrl: api.SiteUrl,
                    Sell: null, Buy: null, TsUtc: null,
                    PrivacyLevel: (api as IPrivacyLevel)?.PrivacyLevel,
                    MinAmountUsd: Positive((api as IMinAmountUsd)?.MinAmountUsd),
                    RateType: rateType
                );
            }
        });

        var rows = await Task.WhenAll(tasks);
        return rows.OrderBy(x => x.Exchange).ToList();
    }

    public async Task<IReadOnlyList<PriceResult>> GetPricesAsync(
        string @base, string quote, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(@base) || string.IsNullOrWhiteSpace(quote))
            return Array.Empty<PriceResult>();

        var baseRef = ParseAsset(@base);
        var quoteRef = ParseAsset(quote);

        var tasks = priceApis.Select(api => GetOneExchangePriceAsync(api, baseRef, quoteRef, ct));
        var results = await Task.WhenAll(tasks);

        return results
            .Where(r => r is not null)
            .Cast<PriceResult>()
            .OrderBy(r => r.Exchange)
            .ToList();
    }

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(
        string exchangeKey, CancellationToken ct = default)
    {
        var currencyApi = currencyApis.FirstOrDefault(x =>
            x.ExchangeKey.Equals(exchangeKey, StringComparison.OrdinalIgnoreCase));

        if (currencyApi is null) return Array.Empty<ExchangeCurrency>();

        var key = $"currencies:{exchangeKey}";
        var ttl = TimeSpan.FromMinutes(Math.Clamp(opt.CurrenciesCacheMinutes, 1, 24 * 60));

        return await GetOrCreateLockedAsync<IReadOnlyList<ExchangeCurrency>>(key, ttl, ct,
                   () => currencyApi.GetCurrenciesAsync(ct))
               ?? Array.Empty<ExchangeCurrency>();
    }

    // ── Quote snapshot assembly (for the Postgres logger) ─────────────────────

    private QuoteSnapshot BuildSnapshot(
        AssetRef baseRef, AssetRef quoteRef, IReadOnlyList<TwoWayPriceRow> rows)
    {
        var pair = BuildPairLabel(baseRef, quoteRef);

        // Only log rows that pass the SAME sanity filter the board/API uses (see
        // IsSaneTwoWayRow). The history rollup the charts read from must never include a quote
        // that isn't "live" on the board — otherwise a single exchange returning a mis-scaled
        // price (e.g. LetsExchange's info-revert buy running ~500x high) corrupts the pooled
        // average and blows up the charts even though the board correctly hides that exchange.
        var maxSpread = this.opt.MaxSpreadFraction;
        var dtoRows = rows
            .Where(r => IsSaneTwoWayRow(r.Buy, r.Sell, maxSpread))
            .Select(r => new QuoteRowDto(
            ExchangeKey: r.Exchange,
            SiteName: r.SiteName,
            SiteUrl: r.SiteUrl,
            PrivacyLevel: r.PrivacyLevel,
            RateType: r.RateType,
            Buy: r.Buy,
            Sell: r.Sell,
            QuoteTsUtc: r.TsUtc
        )).ToList();

        return new QuoteSnapshot(pair, DateTimeOffset.UtcNow, dtoRows);
    }

    /// <summary>Normalized pair label stored in the DB, e.g. "XMR/USDT:Tron".</summary>
    public static string BuildPairLabel(AssetRef baseRef, AssetRef quoteRef)
    {
        static string Part(AssetRef a) =>
            string.IsNullOrWhiteSpace(a.Network) ? a.Ticker : $"{a.Ticker}:{a.Network}";
        return $"{Part(baseRef)}/{Part(quoteRef)}";
    }

    // ── Per-exchange cache helpers ────────────────────────────────────────────
    // These cache individual exchange results so FetchLiveAsync doesn't hammer
    // the exchange APIs on every warmer cycle — it reads from IMemoryCache and
    // only calls the actual API when a per-exchange TTL expires.

    private Task<PriceResult?> GetOneExchangeSellAsync(
        IExchangePriceApi api, AssetRef baseRef, AssetRef quoteRef, decimal? sellProbe, bool fixedRate, CancellationToken ct)
    {
        if (!QuoteSupported(api.ExchangeKey, quoteRef)) return Task.FromResult<PriceResult?>(null);
        var mode = fixedRate ? "fix" : "flt";
        var key = $"sell:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}:{mode}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(opt.PriceCacheSeconds, 1, 300));
        return GetOrCreateLockedAsync<PriceResult?>(key, ttl, ct, async () =>
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(api.ExchangeKey, baseRef, quoteRef, ct);
            // Sell the base (XMR) for ~TargetTradeUsd (probe sized once per cycle by the caller).
            return await api.GetSellPriceAsync(new PriceQuery(rb, rq, sellProbe, Fixed: fixedRate), ct);
        });
    }

    private Task<PriceResult?> GetOneExchangeBuyAsync(
        string exchangeKey, IExchangeBuyPriceApi api,
        AssetRef baseRef, AssetRef quoteRef, decimal? buyProbe, bool fixedRate, CancellationToken ct)
    {
        if (!QuoteSupported(exchangeKey, quoteRef)) return Task.FromResult<PriceResult?>(null);
        var mode = fixedRate ? "fix" : "flt";
        var key = $"buy:{exchangeKey}:{baseRef.Key}->{quoteRef.Key}:{mode}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(opt.PriceCacheSeconds, 1, 300));
        return GetOrCreateLockedAsync<PriceResult?>(key, ttl, ct, async () =>
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(exchangeKey, baseRef, quoteRef, ct);
            // Pay ~TargetTradeUsd of the quote asset to receive XMR — same trade size as the sell side.
            return await api.GetBuyPriceAsync(new PriceQuery(rb, rq, buyProbe, Fixed: fixedRate), ct);
        });
    }

    private Task<PriceResult?> GetOneExchangePriceAsync(
        IExchangePriceApi api, AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        if (!QuoteSupported(api.ExchangeKey, quoteRef)) return Task.FromResult<PriceResult?>(null);
        var key = $"price:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(opt.PriceCacheSeconds, 1, 300));
        return GetOrCreateLockedAsync<PriceResult?>(key, ttl, ct, async () =>
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(api.ExchangeKey, baseRef, quoteRef, ct);
            return await api.GetSellPriceAsync(new PriceQuery(rb, rq), ct);
        });
    }

    private async Task<T?> GetOrCreateLockedAsync<T>(
        string key, TimeSpan ttl, CancellationToken ct, Func<Task<T?>> factory)
    {
        if (cache.TryGetValue(key, out T? cached)) return cached;

        var sem = keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(CancellationToken.None);
        try
        {
            if (cache.TryGetValue(key, out cached)) return cached;
            if (ct.IsCancellationRequested) return default;

            T? value;
            try { value = await factory(); }
            catch (OperationCanceledException) { throw; }
            catch { return default; }

            if (value is not null)
                cache.Set(key, value, new MemoryCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = ttl });

            return value;
        }
        finally { sem.Release(); }
    }

    // ── Currency resolution ───────────────────────────────────────────────────

    private async Task<(AssetRef Base, AssetRef Quote)> ResolveExchangeIdsAsync(
        string exchangeKey, AssetRef @base, AssetRef quote, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(@base.ExchangeId) && !string.IsNullOrWhiteSpace(quote.ExchangeId))
            return (@base, quote);

        var currencies = await GetCurrenciesAsync(exchangeKey, ct);
        if (currencies.Count == 0) return (@base, quote);

        string? baseId = string.IsNullOrWhiteSpace(@base.ExchangeId)
            ? FindExchangeId(currencies, @base.Ticker, @base.Network) : @base.ExchangeId;
        string? quoteId = string.IsNullOrWhiteSpace(quote.ExchangeId)
            ? FindExchangeId(currencies, quote.Ticker, quote.Network) : quote.ExchangeId;

        var resolvedBase = string.IsNullOrWhiteSpace(baseId) ? @base : @base with { ExchangeId = baseId };
        var resolvedQuote = string.IsNullOrWhiteSpace(quoteId) ? quote : quote with { ExchangeId = quoteId };
        return (resolvedBase, resolvedQuote);
    }

    private static string? FindExchangeId(
        IReadOnlyList<ExchangeCurrency> currencies, string ticker, string? network)
    {
        if (!string.IsNullOrWhiteSpace(network))
            return currencies.FirstOrDefault(c =>
                c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                c.Network.Equals(network, StringComparison.OrdinalIgnoreCase))?.ExchangeId;

        return currencies.FirstOrDefault(c =>
            c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))?.ExchangeId;
    }

    private static string PairKey(AssetRef b, AssetRef q) => $"{b.Key}->{q.Key}";

    // ── Per-exchange quote support ────────────────────────────────────────────
    // Some clients were written for XMR/USDT only: they ignore query.Quote and
    // quote against USDT, so for BTC/ETH they return a USD-denominated number
    // mislabeled as the requested quote (e.g. ~318 "BTC"). Until a client is
    // taught to price a quote natively, list the quotes it CAN price here.
    //
    // Absent from this map  = client already honors query.Quote (prices any quote).
    // Present in this map    = client can ONLY price the listed quote tickers;
    //                          for anything else it is skipped and drops off the
    //                          page for that pair (a null/null row is filtered
    //                          out client-side).
    //
    // To enable, say, BTC on an exchange: teach its client to use query.Quote,
    // then add "BTC" to its list here (or remove the entry to allow everything).
    private static readonly IReadOnlyDictionary<string, string[]> QuoteSupport =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["bitxchange"]  = ["USDT", "BTC", "ETH"], // client resolves any pair; two-way XMR↔BTC/ETH
            ["ccecash"]     = ["USDT", "BTC", "ETH"], // USDT fast-path; BTC/ETH via /calculate
            ["changehero"]  = ["USDT"], // excluded entirely via PriceService:ExcludedExchanges (XMR is sell-only here)
            ["cyphergoat"]  = ["USDT", "BTC", "ETH"], // client resolves any pair; two-way XMR↔BTC/ETH
            ["quickex"]     = ["USDT", "BTC", "ETH"], // instruments-resolved; two-way XMR↔BTC/ETH
            ["sageswap"]    = ["USDT", "BTC", "ETH"], // BestChange feed has XMR↔BTC/ETH rows
            ["secureshift"] = ["USDT"],
            ["stereoswap"]  = ["USDT", "BTC", "ETH"], // client resolves any pair; two-way XMR↔BTC/ETH
            ["swapgate"]    = ["USDT", "BTC", "ETH"], // client resolves any pair via instruments; two-way XMR↔BTC/ETH
            ["godex"]       = ["USDT", "BTC"], // BTC two-way; ETH is sell-only on GoDex (ETH→XMR unavailable)
            ["octoswap"]    = ["USDT", "BTC", "ETH"], // client resolves any pair; two-way XMR↔BTC/ETH
            ["trocador"]    = ["USDT", "BTC", "ETH"], // aggregator; two-way XMR↔BTC/ETH (ETH=ERC20)
            ["xgram"]       = ["USDT", "BTC", "ETH"], // client resolves any pair; two-way XMR↔BTC/ETH
            ["flashift"]    = ["USDT", "BTC", "ETH"], // aggregator; two-way XMR↔BTC/ETH/USDT (USDT=trx/Tron)

            // WizardSwap has no USDT listing — it can only price crypto quotes.
            // Restrict it to BTC/ETH so it surfaces on those pages and is skipped
            // (drops off) on the USDT page instead of firing a doomed estimate.
            ["wizardswap"]  = ["BTC", "ETH"],

            // xChange.me can only SEND ~10 coins (no USDT), so USDT is sell-only there.
            // Restrict to BTC/ETH — the only pairs where BOTH buy and sell work — so we
            // never surface a one-sided USDT row.
            ["xchange"]     = ["BTC", "ETH"],
        };

    private static bool QuoteSupported(string exchangeKey, AssetRef quote)
        => !QuoteSupport.TryGetValue(exchangeKey, out var allowed)
           || allowed.Contains(quote.Ticker ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    // ── $-standardized probe sizing ───────────────────────────────────────────
    // Every quote is taken for a trade worth opt.TargetTradeUsd (default $2,500) so all
    // exchanges — and both the buy and sell sides — are measured at the same, realistic size.
    // Small trades quote a worse effective rate (fixed network/withdrawal fees weigh more
    // heavily); the rate flattens out by a few thousand dollars.

    private static readonly HashSet<string> StableTickers =
        new(StringComparer.OrdinalIgnoreCase) { "USDT", "USDC", "DAI", "USD", "BUSD", "TUSD" };

    // Sell probe: TargetTradeUsd worth of the base asset (XMR). Falls back to a configured
    // XMR amount if the live price can't be fetched, so quoting never stops.
    private async Task<decimal?> SellProbeAsync(AssetRef baseRef, CancellationToken ct)
    {
        var ticker = (baseRef.Ticker ?? "").Trim().ToUpperInvariant();
        if (StableTickers.Contains(ticker)) return opt.TargetTradeUsd;
        var usd = await GetUsdPriceAsync(ticker, ct);
        return usd is decimal p && p > 0 ? RoundProbe(opt.TargetTradeUsd / p) : opt.FallbackSellProbeXmr;
    }

    // Round to 8 decimals: a raw `TargetTradeUsd / price` decimal carries ~27 digits, and some
    // exchange APIs reject an amount with more than 8 dp (e.g. ETZ-Swap → validation error → the
    // quote silently drops). 8 dp is within every asset's native precision and plenty for sizing.
    private static decimal RoundProbe(decimal v) => Math.Round(v, 8, MidpointRounding.AwayFromZero);

    // Buy probe: TargetTradeUsd worth of the quote asset paid to receive XMR. Stablecoins are
    // ~$1; for crypto quotes fall back to the old fixed sizes if the price is unavailable.
    private async Task<decimal?> BuyProbeAsync(AssetRef quote, CancellationToken ct)
    {
        var ticker = (quote.Ticker ?? "").Trim().ToUpperInvariant();
        if (StableTickers.Contains(ticker)) return opt.TargetTradeUsd;
        var usd = await GetUsdPriceAsync(ticker, ct);
        if (usd is decimal p && p > 0) return RoundProbe(opt.TargetTradeUsd / p);
        return ticker switch { "BTC" => 0.025m, "ETH" => 0.75m, _ => null };
    }

    // USD spot price for a ticker via CoinGecko (free, no key), cached briefly. Only used to
    // size probes, so a miss just falls back to a fixed size — never blocks a quote. For
    // ambiguous tickers it takes the highest-market-cap match.
    private async Task<decimal?> GetUsdPriceAsync(string ticker, CancellationToken ct)
    {
        ticker = (ticker ?? "").Trim().ToLowerInvariant();
        if (ticker.Length == 0) return null;

        var ttl = TimeSpan.FromMinutes(Math.Clamp(opt.UsdPriceCacheMinutes, 1, 60));
        return await GetOrCreateLockedAsync<decimal?>($"usd:{ticker}", ttl, ct, async () =>
        {
            // Returns 0m (not null) on any failure so the miss is CACHED — CoinGecko rate-limits
            // (HTTP 429) the VPS, and an uncached miss would refetch every cycle. Callers treat
            // 0 as "unknown" and fall back to a fixed probe size, so quoting is never blocked.
            try
            {
                var url = "https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&symbols="
                          + Uri.EscapeDataString(ticker);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("MoneroPriceNow/1.0 (+https://moneropricenow.com)");

                using var http = httpFactory.CreateClient();
                http.Timeout = TimeSpan.FromSeconds(8);
                using var res = await http.SendAsync(req, ct);
                if (!res.IsSuccessStatusCode) return 0m;

                var body = await res.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return 0m;

                decimal best = 0; double bestCap = -1;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("current_price", out var cp) ||
                        cp.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
                    double cap = el.TryGetProperty("market_cap", out var mc) &&
                                 mc.ValueKind == System.Text.Json.JsonValueKind.Number ? mc.GetDouble() : 0;
                    if (cap > bestCap) { bestCap = cap; best = cp.GetDecimal(); }
                }
                return best; // 0 if nothing usable — cached as a miss
            }
            catch
            {
                return 0m;
            }
        });
    }



    private static AssetRef ParseAsset(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new AssetRef("");
        var t = s.Trim();
        var upper = t.Replace("_", "").Replace("-", "").Replace(":", "").ToUpperInvariant();

        if (upper is "USDTTRC" or "USDTTRX") return new AssetRef("USDT", "Tron");
        if (upper is "USDTERC" or "USDTETH") return new AssetRef("USDT", "Ethereum");
        if (upper is "USDTSOL") return new AssetRef("USDT", "Solana");
        if (upper is "USDTBSC") return new AssetRef("USDT", "Binance Smart Chain");

        var parts = t.Split(':', 2,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) return new AssetRef(parts[0].ToUpperInvariant(), parts[1]);
        return new AssetRef(t.ToUpperInvariant());
    }
}