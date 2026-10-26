using CryptoPriceNow.Data.Models;
using CryptoPriceNow.Data.Services;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Microsoft.Extensions.Caching.Memory;

namespace CryptoPriceNow.Web.Support;

/// <summary>
/// "Monero Millionaire": how much XMR is worth 1,000,000 USDT right now, and what it took
/// 1, 7, 30 and 90 days ago. The live price is the same board mid as the homepage headline
/// (<see cref="BoardMid.FloatMid"/>); the past prices come from the pooled float-rate
/// price history (PriceBuckets).
/// </summary>
public sealed class MillionaireService
{
    public const decimal TargetUsdt = 1_000_000m;

    private const string HistoryCacheKey = "millionaire:history";
    private const string NowCacheKey = "millionaire:now";

    private static readonly (string Label, int Days)[] Lookbacks =
    {
        ("1 day ago", 1), ("7 days ago", 7), ("30 days ago", 30), ("90 days ago", 90),
    };

    private readonly IPriceService _prices;
    private readonly IServiceProvider _services;
    private readonly IMemoryCache _cache;

    public MillionaireService(IPriceService prices, IServiceProvider services, IMemoryCache cache)
    {
        _prices = prices;
        _services = services;
        _cache = cache;
    }

    public sealed record Comparison(string Label, int Days, decimal? Price, decimal? Xmr);

    public sealed record SeriesPoint(long T, decimal Xmr);

    public sealed record Snapshot(
        decimal? Price,
        decimal? Xmr,
        DateTimeOffset UpdatedUtc,
        IReadOnlyList<Comparison> Comparisons,
        IReadOnlyList<SeriesPoint> Series);

    /// <summary>XMR needed for 1,000,000 USDT at <paramref name="price"/>, rounded to 2 decimals.</summary>
    public static decimal? XmrNeeded(decimal? price) =>
        price is decimal p && p > 0m ? Math.Round(TargetUsdt / p, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// The market price of the bucket closest to <paramref name="whenUtc"/>, or null when the
    /// nearest bucket is further away than <paramref name="tolerance"/> (no data that far back).
    /// </summary>
    public static decimal? PriceAt(IReadOnlyList<HistoryPoint> points, DateTimeOffset whenUtc, TimeSpan tolerance)
    {
        HistoryPoint? best = null;
        var bestGap = TimeSpan.MaxValue;
        foreach (var p in points)
        {
            if (p.Market is not decimal m || m <= 0m)
            {
                continue;
            }

            var gap = (p.BucketUtc - whenUtc).Duration();
            if (gap < bestGap)
            {
                best = p;
                bestGap = gap;
            }
        }

        return best != null && bestGap <= tolerance ? best.Market : null;
    }

    public async Task<Snapshot> GetAsync(CancellationToken ct)
    {
        var price = await GetCurrentPriceAsync(ct);
        var (comparisons, series) = await GetHistoryAsync(ct);
        return new Snapshot(price, XmrNeeded(price), DateTimeOffset.UtcNow, comparisons, series);
    }

    // Same number as the homepage headline; cached briefly so many open tabs share one lookup.
    private async Task<decimal?> GetCurrentPriceAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(NowCacheKey, out decimal? cached))
        {
            return cached;
        }

        var pair = PairCatalog.Usdt;
        decimal? mid = null;
        try
        {
            var rows = await _prices.GetTwoWayPricesAsync(pair.Base, pair.ApiQuote, ct);
            mid = BoardMid.FloatMid(rows, pair.MinValidPrice);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Exchanges unreachable: show "--" for now rather than an error page.
        }

        if (mid is not null)
        {
            _cache.Set(NowCacheKey, mid, TimeSpan.FromSeconds(10));
        }

        return mid;
    }

    // Past prices move slowly, so the DB is read at most every 5 minutes.
    private async Task<(IReadOnlyList<Comparison>, IReadOnlyList<SeriesPoint>)> GetHistoryAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(HistoryCacheKey, out (IReadOnlyList<Comparison>, IReadOnlyList<SeriesPoint>) cached))
        {
            return cached;
        }

        var history = _services.GetService<PriceHistoryService>();
        if (history is null)
        {
            return (Lookbacks.Select(l => new Comparison(l.Label, l.Days, null, null)).ToList(), Array.Empty<SeriesPoint>());
        }

        var pair = PairCatalog.Usdt.HistoryPair;
        var now = DateTimeOffset.UtcNow;
        try
        {
            // 7-day view (2-hour buckets) for the recent points; 90-day view (daily buckets)
            // for the older ones and the chart.
            var week = await history.GetHistoryAsync(pair, "7d", "float", ct);
            var quarter = await history.GetHistoryAsync(pair, "90d", "float", ct);

            var comparisons = Lookbacks.Select(l =>
            {
                var when = now.AddDays(-l.Days);
                var price = l.Days <= 7
                    ? PriceAt(week.Points, when, TimeSpan.FromHours(3))
                    : PriceAt(quarter.Points, when, TimeSpan.FromDays(1.5));
                return new Comparison(l.Label, l.Days, price, XmrNeeded(price));
            }).ToList();

            var series = quarter.Points
                .Where(p => p.Market is decimal m && m > 0m)
                .Select(p => new SeriesPoint(p.BucketUtc.ToUnixTimeMilliseconds(), XmrNeeded(p.Market)!.Value))
                .ToList();

            var result = ((IReadOnlyList<Comparison>)comparisons, (IReadOnlyList<SeriesPoint>)series);
            _cache.Set(HistoryCacheKey, result, TimeSpan.FromMinutes(5));
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // DB briefly unreachable: page still shows the live number.
            return (Lookbacks.Select(l => new Comparison(l.Label, l.Days, null, null)).ToList(), Array.Empty<SeriesPoint>());
        }
    }
}
