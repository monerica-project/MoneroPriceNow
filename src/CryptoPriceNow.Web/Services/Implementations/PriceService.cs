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
        IPriceQuoteSink quoteSink)
    {
        this.opt = options.Value;

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
            cache.Remove($"sell:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}");
            cache.Remove($"price:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}");
            if (api is IExchangeBuyPriceApi)
                cache.Remove($"buy:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}");
        }

        var rows = await FetchLiveAsync(baseRef, quoteRef, ct);
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

    private Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesInternalAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        // Fast path: warmer has already built a snapshot — return it instantly
        if (latestRows.TryGetValue(PairKey(baseRef, quoteRef), out var cached))
            return Task.FromResult(cached);

        // Cold start only (first request before warmer has finished its first run)
        return FetchLiveAsync(baseRef, quoteRef, ct);
    }

    // ── Live fetch (calls exchange APIs in parallel, respects per-exchange TTL) 
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(8);
    private async Task<IReadOnlyList<TwoWayPriceRow>> FetchLiveAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        var tasks = priceApis.Select(async api =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ExchangeTimeout);

            try
            {
                var sellRes = await GetOneExchangeSellAsync(api, baseRef, quoteRef, cts.Token);
                var buyRes = api is IExchangeBuyPriceApi buyApi
                    ? await GetOneExchangeBuyAsync(api.ExchangeKey, buyApi, baseRef, quoteRef, cts.Token)
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
                    MinAmountUsd: sellRes?.MinAmountUsd
                                  ?? buyRes?.MinAmountUsd
                                  ?? (api as IMinAmountUsd)?.MinAmountUsd
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
                    MinAmountUsd: (api as IMinAmountUsd)?.MinAmountUsd
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
        var dtoRows = rows.Select(r => new QuoteRowDto(
            ExchangeKey: r.Exchange,
            SiteName: r.SiteName,
            SiteUrl: r.SiteUrl,
            PrivacyLevel: r.PrivacyLevel,
            RateType: rateTypes.TryGetValue(r.Exchange, out var rt) ? rt : RateTypes.Float,
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
        IExchangePriceApi api, AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        if (!QuoteSupported(api.ExchangeKey, quoteRef)) return Task.FromResult<PriceResult?>(null);
        var key = $"sell:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(opt.PriceCacheSeconds, 1, 300));
        return GetOrCreateLockedAsync<PriceResult?>(key, ttl, ct, async () =>
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(api.ExchangeKey, baseRef, quoteRef, ct);
            return await api.GetSellPriceAsync(new PriceQuery(rb, rq), ct);
        });
    }

    private Task<PriceResult?> GetOneExchangeBuyAsync(
        string exchangeKey, IExchangeBuyPriceApi api,
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        if (!QuoteSupported(exchangeKey, quoteRef)) return Task.FromResult<PriceResult?>(null);
        var key = $"buy:{exchangeKey}:{baseRef.Key}->{quoteRef.Key}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(opt.PriceCacheSeconds, 1, 300));
        return GetOrCreateLockedAsync<PriceResult?>(key, ttl, ct, async () =>
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(exchangeKey, baseRef, quoteRef, ct);
            return await api.GetBuyPriceAsync(new PriceQuery(rb, rq, ProbeForQuote(rq)), ct);
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
            ["bitxchange"]  = ["USDT"],
            ["ccecash"]     = ["USDT"], // honors query.Quote in code but still emits USD for BTC/ETH — confirm before enabling
            ["changehero"]  = ["USDT"], // excluded entirely via PriceService:ExcludedExchanges (XMR is sell-only here)
            ["cyphergoat"]  = ["USDT"],
            ["quickex"]     = ["USDT"],
            ["sageswap"]    = ["USDT"],
            ["secureshift"] = ["USDT"],
            ["stereoswap"]  = ["USDT"],
            ["swapgate"]    = ["USDT"],
            ["swapter"]     = ["USDT"],
            ["trocador"]    = ["USDT"],
            ["xgram"]       = ["USDT"],

            // WizardSwap has no USDT listing — it can only price crypto quotes.
            // Restrict it to BTC/ETH so it surfaces on those pages and is skipped
            // (drops off) on the USDT page instead of firing a doomed estimate.
            ["wizardswap"]  = ["BTC", "ETH"],
        };

    private static bool QuoteSupported(string exchangeKey, AssetRef quote)
        => !QuoteSupport.TryGetValue(exchangeKey, out var allowed)
           || allowed.Contains(quote.Ticker ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    // Buy-side probe size, denominated in the quote currency, handed to clients
    // that quote "buy" by sending a fixed amount and reading XMR out. Their own
    // defaults are sized for USDT (~100–500), which is absurd as "500 BTC" and
    // overshoots every max — so for crypto quotes we supply a sane amount here.
    // null = leave the client's own default (USDT path unchanged). These are
    // ~a few hundred USD worth at typical rates; tune per quote as needed.
    private static decimal? ProbeForQuote(AssetRef quote) =>
        (quote.Ticker ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "BTC" => 0.01m,
            "ETH" => 0.3m,
            _ => null, // USDT / stablecoins: keep the client's existing default
        };



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