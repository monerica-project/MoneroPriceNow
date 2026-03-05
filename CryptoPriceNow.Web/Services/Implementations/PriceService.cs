using System.Collections.Concurrent;
using CryptoPriceNow.Web.Models;
using ExchangeServices.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CryptoPriceNow.Services;

public sealed class PriceService : IPriceService
{
    private readonly IReadOnlyList<IExchangePriceApi> priceApis;
    private readonly IReadOnlyList<IExchangeCurrencyApi> currencyApis;
    private readonly IMemoryCache cache;
    private readonly PriceServiceOptions opt;

    // One semaphore per cache key — prevents multiple concurrent callers from all
    // executing the factory simultaneously on a cache miss (thundering herd).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> keyLocks = new();

    public PriceService(
        IEnumerable<IExchangePriceApi> priceApis,
        IEnumerable<IExchangeCurrencyApi> currencyApis,
        IMemoryCache cache,
        IOptions<PriceServiceOptions> options)
    {
        this.priceApis = priceApis.ToList();
        this.currencyApis = currencyApis.ToList();
        this.cache = cache;
        this.opt = options.Value;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Public utility methods (used by PriceWarmingService)
    // -------------------------------------------------------------------------

    /// <summary>Public wrapper so background services can parse an asset string.</summary>
    public static AssetRef ParseAssetPublic(string s) => ParseAsset(s);

    /// <summary>
    /// Evicts all cached sell/buy prices for the given pair and re-fetches them
    /// immediately. Called by PriceWarmingService to pre-warm the cache.
    /// </summary>
    public async Task ForceRefreshAllAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct = default)
    {
        foreach (var api in this.priceApis)
        {
            var sellKey = $"sell:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}";
            var priceKey = $"price:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}";
            cache.Remove(sellKey);
            cache.Remove(priceKey);

            if (api is IExchangeBuyPriceApi)
            {
                var buyKey = $"buy:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}";
                cache.Remove(buyKey);
            }
        }

        // Re-fetch everything now (results go straight into cache)
        await GetTwoWayPricesAsync(baseRef, quoteRef, ct);
    }



    public Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesAsync(
        string @base, string quote, CancellationToken ct = default)
        => GetTwoWayPricesInternalAsync(ParseAsset(@base), ParseAsset(quote), ct);

    public Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct = default)
        => GetTwoWayPricesInternalAsync(baseRef, quoteRef, ct);

    private async Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesInternalAsync(
        AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        var tasks = this.priceApis.Select(async api =>
        {
            var sellRes = await GetOneExchangeSellAsync(api, baseRef, quoteRef, ct);

            var buyRes = api is IExchangeBuyPriceApi buyApi
                ? await GetOneExchangeBuyAsync(api.ExchangeKey, buyApi, baseRef, quoteRef, ct)
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
                TsUtc: ts
            );
        });

        var rows = await Task.WhenAll(tasks);

        return rows
            .OrderBy(x => x.Exchange)
            .ToList();
    }

    public async Task<IReadOnlyList<PriceResult>> GetPricesAsync(
        string @base, string quote, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(@base) || string.IsNullOrWhiteSpace(quote))
            return Array.Empty<PriceResult>();

        var baseRef = ParseAsset(@base);
        var quoteRef = ParseAsset(quote);

        var tasks = this.priceApis.Select(api =>
            GetOneExchangePriceAsync(api, baseRef, quoteRef, ct));
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
        var currencyApi = this.currencyApis.FirstOrDefault(x =>
            x.ExchangeKey.Equals(exchangeKey, StringComparison.OrdinalIgnoreCase));

        if (currencyApi is null) return Array.Empty<ExchangeCurrency>();

        var key = $"currencies:{exchangeKey}";
        var ttl = TimeSpan.FromMinutes(Math.Clamp(opt.CurrenciesCacheMinutes, 1, 24 * 60));

        return await GetOrCreateLockedAsync<IReadOnlyList<ExchangeCurrency>>(key, ttl, ct,
                   () => currencyApi.GetCurrenciesAsync(ct))
               ?? Array.Empty<ExchangeCurrency>();
    }

    // -------------------------------------------------------------------------
    // Private fetch helpers
    // -------------------------------------------------------------------------

    private Task<PriceResult?> GetOneExchangeSellAsync(
        IExchangePriceApi api, AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
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
        var key = $"buy:{exchangeKey}:{baseRef.Key}->{quoteRef.Key}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(opt.PriceCacheSeconds, 1, 300));

        return GetOrCreateLockedAsync<PriceResult?>(key, ttl, ct, async () =>
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(exchangeKey, baseRef, quoteRef, ct);
            return await api.GetBuyPriceAsync(new PriceQuery(rb, rq), ct);
        });
    }

    private Task<PriceResult?> GetOneExchangePriceAsync(
        IExchangePriceApi api, AssetRef baseRef, AssetRef quoteRef, CancellationToken ct)
    {
        var key = $"price:{api.ExchangeKey}:{baseRef.Key}->{quoteRef.Key}";
        var ttl = TimeSpan.FromSeconds(Math.Clamp(opt.PriceCacheSeconds, 1, 300));

        return GetOrCreateLockedAsync<PriceResult?>(key, ttl, ct, async () =>
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(api.ExchangeKey, baseRef, quoteRef, ct);
            return await api.GetSellPriceAsync(new PriceQuery(rb, rq), ct);
        });
    }

    // -------------------------------------------------------------------------
    // Double-checked locking cache helper
    //
    // IMemoryCache.GetOrCreateAsync does NOT prevent concurrent callers from all
    // executing the factory at once on a cache miss. Under load this causes a
    // "thundering herd" — all N concurrent requests simultaneously hit every
    // exchange API when the cache expires.
    //
    // This method:
    //   1. Fast path: return immediately if already cached (no lock needed).
    //   2. Slow path: acquire a per-key semaphore, double-check the cache,
    //      then only ONE caller executes the factory; all others wait and then
    //      read the value the winner wrote.
    //   3. Exceptions from the factory are never cached — next caller retries.
    //   4. Null results are never cached — a failed/empty exchange response
    //      doesn't poison the cache slot for the full TTL.
    // -------------------------------------------------------------------------
    private async Task<T?> GetOrCreateLockedAsync<T>(
        string key,
        TimeSpan ttl,
        CancellationToken ct,
        Func<Task<T?>> factory)
    {
        // Fast path — no lock required
        if (cache.TryGetValue(key, out T? cached))
            return cached;

        var sem = keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await sem.WaitAsync(ct);
        try
        {
            // Double-check: another caller may have populated the cache
            // while we were waiting for the semaphore
            if (cache.TryGetValue(key, out cached))
                return cached;

            T? value;
            try
            {
                value = await factory();
            }
            catch
            {
                // Don't cache exceptions — next caller gets a fresh attempt
                return default;
            }

            // Only cache non-null successes — transient failures shouldn't
            // block good data from appearing on the next request
            if (value is not null)
            {
                cache.Set(key, value, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl
                });
            }

            return value;
        }
        finally
        {
            sem.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Currency resolution
    // -------------------------------------------------------------------------

    private async Task<(AssetRef Base, AssetRef Quote)> ResolveExchangeIdsAsync(
        string exchangeKey, AssetRef @base, AssetRef quote, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(@base.ExchangeId) && !string.IsNullOrWhiteSpace(quote.ExchangeId))
            return (@base, quote);

        var currencies = await GetCurrenciesAsync(exchangeKey, ct);
        if (currencies.Count == 0) return (@base, quote);

        string? baseId = string.IsNullOrWhiteSpace(@base.ExchangeId)
            ? FindExchangeId(currencies, @base.Ticker, @base.Network)
            : @base.ExchangeId;

        string? quoteId = string.IsNullOrWhiteSpace(quote.ExchangeId)
            ? FindExchangeId(currencies, quote.Ticker, quote.Network)
            : quote.ExchangeId;

        var resolvedBase = string.IsNullOrWhiteSpace(baseId) ? @base : @base with { ExchangeId = baseId };
        var resolvedQuote = string.IsNullOrWhiteSpace(quoteId) ? quote : quote with { ExchangeId = quoteId };

        return (resolvedBase, resolvedQuote);
    }

    private static string? FindExchangeId(
        IReadOnlyList<ExchangeCurrency> currencies, string ticker, string? network)
    {
        if (!string.IsNullOrWhiteSpace(network))
        {
            return currencies.FirstOrDefault(c =>
                c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                c.Network.Equals(network, StringComparison.OrdinalIgnoreCase))?.ExchangeId;
        }

        return currencies.FirstOrDefault(c =>
            c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))?.ExchangeId;
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
        if (parts.Length == 2)
            return new AssetRef(parts[0].ToUpperInvariant(), parts[1]);

        return new AssetRef(t.ToUpperInvariant());
    }
}