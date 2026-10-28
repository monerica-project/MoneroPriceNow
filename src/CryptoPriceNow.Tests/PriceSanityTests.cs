using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Xunit;

namespace CryptoPriceNow.Tests;

/// <summary>Board/chart sanity: zero prices are rejected and a mis-scaled exchange is dropped by the median filter.</summary>
public class PriceSanityTests
{
    private static TwoWayPriceRow Row(string ex, decimal? sell, decimal? buy) =>
        new(ex, ex, null, sell, buy, DateTimeOffset.UtcNow, 'B', null);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 100)]
    [InlineData(-5, -1)]
    public void Zero_or_negative_prices_are_not_sane(decimal sell, decimal buy)
    {
        Assert.False(PriceService.IsSaneTwoWayRow(buy, sell, 0.25m));
    }

    [Fact]
    public void Normal_row_is_sane()
    {
        Assert.True(PriceService.IsSaneTwoWayRow(330m, 320m, 0.25m));
    }

    [Fact]
    public void Mis_scaled_exchange_is_dropped_by_the_median_filter()
    {
        var rows = new[]
        {
            Row("a", 320m, 330m),
            Row("b", 318m, 331m),
            Row("c", 322m, 335m),
            Row("bad", 3200m, 3300m), // both sides 10x: passes the per-row check, skews the mean
            Row("low", 0.3m, 0.31m),   // both sides ~1000x low
        };

        var kept = PriceService.DropPriceOutliers(rows).Select(r => r.Exchange).ToList();

        Assert.Equal(new[] { "a", "b", "c" }, kept);
    }

    [Fact]
    public void Median_filter_needs_three_rows()
    {
        var rows = new[] { Row("a", 320m, 330m), Row("bad", 3200m, 3300m) };
        Assert.Equal(2, PriceService.DropPriceOutliers(rows).Count());
    }

    [Fact]
    public void Normal_spread_between_exchanges_is_kept()
    {
        var rows = new[] { Row("a", 300m, 320m), Row("b", 320m, 345m), Row("c", 310m, 330m), Row("d", 340m, 365m) };
        Assert.Equal(4, PriceService.DropPriceOutliers(rows).Count());
    }
}
