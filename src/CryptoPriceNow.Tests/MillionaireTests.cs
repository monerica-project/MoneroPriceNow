using Xunit;
using CryptoPriceNow.Data.Models;
using CryptoPriceNow.Web.Support;

namespace CryptoPriceNow.Tests;

// /millionaire: XMR needed for 1,000,000 USDT, and the price on a past day.
public class MillionaireTests
{
    private static readonly DateTimeOffset Now = new (2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void XmrNeeded_IsOneMillionOverPrice_RoundedTo2()
    {
        Assert.Equal(3000.00m, MillionaireService.XmrNeeded(333.3333334m));
        Assert.Equal(2857.14m, MillionaireService.XmrNeeded(350m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void XmrNeeded_NoPrice_IsNull(int? price) =>
        Assert.Null(MillionaireService.XmrNeeded(price));

    [Fact]
    public void PriceAt_PicksClosestBucket_WithinTolerance()
    {
        var pts = new List<HistoryPoint>
        {
            new (Now.AddDays(-8), 300m, 290m, 295m, 10),
            new (Now.AddDays(-7).AddHours(1), 310m, 300m, 305m, 10),
            new (Now.AddDays(-6), 320m, 310m, 315m, 10),
            new (Now.AddDays(-7), null, null, null, 0), // empty bucket ignored
        };

        Assert.Equal(305m, MillionaireService.PriceAt(pts, Now.AddDays(-7), TimeSpan.FromHours(3)));
        Assert.Null(MillionaireService.PriceAt(pts, Now.AddDays(-30), TimeSpan.FromDays(1.5)));
    }
}
