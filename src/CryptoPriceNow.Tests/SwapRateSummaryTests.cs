using CryptoPriceNow.Web.Services;
using Xunit;

namespace CryptoPriceNow.Tests;

public class SwapRateSummaryTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    // Five exchanges for 48 hours: A always best, B 0.4% behind, C and D 1% behind, E 2% behind.
    private static List<SwapRateSummary.HourQuote> Quotes(Action<List<SwapRateSummary.HourQuote>, DateTime>? extra = null)
    {
        var list = new List<SwapRateSummary.HourQuote>();
        for (int i = 0; i < 48; i++)
        {
            var h = Now.AddHours(-i);
            foreach (var (key, sell) in new[] { ("a", 400m), ("b", 398.4m), ("c", 396m), ("d", 396m), ("e", 392m) })
            {
                list.Add(new SwapRateSummary.HourQuote(key, key.ToUpperInvariant() + "Swap", "XMR/USDT:Tron", h, 410m, sell));
            }

            extra?.Invoke(list, h);
        }

        return list;
    }

    [Fact]
    public void TypicalQuoteTrailsTheBest_AndExchangesAreRanked()
    {
        var doc = SwapRateSummary.Build(Quotes(), Now, 30);
        var sell = doc.Markets.Single(m => m.Side == "sell");

        Assert.Equal("USDT", sell.Quote);
        Assert.Equal(48, sell.Hours);
        Assert.Equal(5m, sell.Depth);
        Assert.Equal(1.000m, sell.EdgePct);            // median quote 396 vs best 400
        Assert.Equal(1.000m, sell.EdgePct7d);
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, sell.Exchanges.Select(e => e.Key).OrderBy(k => k));
        Assert.Equal("a", sell.Exchanges[0].Key);
        Assert.Equal(100m, sell.Exchanges[0].BestPct);
        Assert.Equal(100m, sell.Exchanges.Single(e => e.Key == "b").NearBestPct);   // 0.4% behind = near
        Assert.Equal(0m, sell.Exchanges.Single(e => e.Key == "c").NearBestPct);     // 1% behind = not near
        Assert.Equal(2.000m, sell.Exchanges.Single(e => e.Key == "e").MedianGapPct);
        Assert.Equal(400m, sell.Daily[^1].BestPrice);
        Assert.Equal(396m, sell.Daily[^1].MedianPrice);

        // Buying: every exchange asks 410, so nobody trails.
        Assert.Equal(0m, doc.Markets.Single(m => m.Side == "buy").EdgePct);
    }

    [Fact]
    public void GlitchedQuote_IsDropped_AndDoesNotBecomeTheBest()
    {
        var doc = SwapRateSummary.Build(Quotes((list, h) => list.Add(new SwapRateSummary.HourQuote("x", "Broken", "XMR/USDT:Tron", h, 410m, 4000m))), Now, 30);
        var sell = doc.Markets.Single(m => m.Side == "sell");
        Assert.Equal(400m, sell.Daily[^1].BestPrice);
        Assert.DoesNotContain(sell.Exchanges, e => e.Key == "x");
    }

    [Fact]
    public void HoursWithFewerThanFourExchanges_AreNotCounted()
    {
        var few = Quotes().Where(q => q.ExchangeKey is "a" or "b" or "c").ToList();
        Assert.Empty(SwapRateSummary.Build(few, Now, 30).Markets);
    }

    [Fact]
    public void ExchangeThatStoppedQuoting_IsLeftOut()
    {
        // "old" quoted well, but only until five days ago.
        var doc = SwapRateSummary.Build(Quotes((list, h) =>
        {
            if (h < Now.AddDays(-1).AddHours(-12))
            {
                list.Add(new SwapRateSummary.HourQuote("old", "OldSwap", "XMR/USDT:Tron", h.AddDays(-4), 410m, 400m));
            }
        }).Concat(Enumerable.Range(0, 48).SelectMany(i => new[] { "a", "b", "c", "d" }.Select(k =>
            new SwapRateSummary.HourQuote(k, k, "XMR/USDT:Tron", Now.AddDays(-5).AddHours(-i), 410m, 399m)))), Now, 30);
        Assert.DoesNotContain(doc.Markets.Single(m => m.Side == "sell").Exchanges, e => e.Key == "old");
    }

    [Fact]
    public void Json_UsesSnakeCaseNames()
    {
        var json = SwapRateSummary.ToJson(SwapRateSummary.Build(Quotes(), Now, 30));
        Assert.Contains("\"edge_pct\":", json);
        Assert.Contains("\"edge_pct_7d\":", json);
        Assert.Contains("\"near_best_pct\":", json);
        Assert.Contains("\"generated_utc\":\"2026-11-26T12:00:00Z\"", json);
    }
}
