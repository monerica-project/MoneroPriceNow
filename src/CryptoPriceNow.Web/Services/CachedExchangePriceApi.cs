using ExchangeServices.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace CryptoPriceNow.Web.Services;

public sealed class CachedExchangePriceApi : IExchangePriceApi
{
    private readonly IExchangePriceApi inner;
    private readonly IMemoryCache cache;
    private readonly TimeSpan ttl;

    public string ExchangeKey => inner.ExchangeKey;

    public string SiteName => inner.SiteName;

    public string? SiteUrl => inner.SiteUrl;

    public CachedExchangePriceApi(IExchangePriceApi inner, IMemoryCache cache, TimeSpan ttl)
    {
        this.inner = inner;
        this.cache = cache;
        this.ttl = ttl;
    }

    public Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var key = $"price:{inner.ExchangeKey}:{query.Key}";

        return cache.GetOrCreateAsync<PriceResult?>(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            return await inner.GetSellPriceAsync(query, ct);
        })!;
    }
}