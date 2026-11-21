using CryptoPriceNow.Web.Support;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CryptoPriceNow.Tests;

/// <summary>Swap fan-out limits: amount snapping, the per-minute budget and shared runs.</summary>
public class SwapBudgetTests
{
    // An exchange that quotes 1 XMR = 0.004 BTC after a short delay, and counts its calls.
    private sealed class SlowApi : IExchangePriceApi, IExchangeCurrencyApi, IPrivacyLevel
    {
        public int PriceCalls;
        public TimeSpan Delay = TimeSpan.FromMilliseconds(50);

        public string ExchangeKey => "slow";
        public string SiteName => "Slow";
        public string? SiteUrl => "javascript:alert(1)";
        public char PrivacyLevel => 'A';

        public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PriceCalls);
            await Task.Delay(Delay, ct);
            return new PriceResult("slow", query.Base, query.Quote, 0.004m, DateTimeOffset.UtcNow);
        }

        public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }

    private static SwapQuoteService NewSwap(SlowApi api, int perMinute = 20) => new(
        new IExchangePriceApi[] { api },
        new IExchangeCurrencyApi[] { api },
        new MemoryCache(new MemoryCacheOptions()),
        new NoHttp(),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Swap:FanOutsPerMinute"] = perMinute.ToString() })
            .Build());

    [Theory]
    [InlineData("btc", 0.1, 0.1)]
    [InlineData("btc", 0.11, 0.1)]
    [InlineData("btc", 0.37, 0.4)]
    [InlineData("btc", 0.000001, 0.0001)]   // below the range: quoted at the bottom edge
    [InlineData("btc", 500, 20)]           // above the range: quoted at the top edge
    [InlineData("usdt", 1, 10)]
    [InlineData("usdt", 999_999_999, 1_000_000)]
    [InlineData("xmr", 7.1, 8)]
    public void SnapAmount_clamps_and_snaps_to_steps(string from, double amount, double expected)
        => Assert.Equal(ExchangeServices.DecimalConvert.FromDouble(expected), SwapQuoteService.SnapAmount(from, ExchangeServices.DecimalConvert.FromDouble(amount)));

    [Fact]
    public void SnapAmount_gives_few_distinct_keys_across_the_whole_range()
    {
        var keys = new HashSet<decimal>();
        for (var a = 0.00001m; a < 1000m; a *= 1.01m)
        {
            keys.Add(SwapQuoteService.SnapAmount("btc", a));
        }

        Assert.InRange(keys.Count, 1, 70);
    }

    [Fact]
    public void Budget_refuses_fresh_fan_outs_past_the_per_minute_limit_with_busy()
    {
        var api = new SlowApi { Delay = TimeSpan.FromSeconds(1) };
        var swap = NewSwap(api, perMinute: 3);
        var amounts = new[] { 1m, 2m, 3m, 4m };

        var sessions = amounts.Select(a => swap.Start("xmr", "btc", a)).ToList();

        Assert.All(sessions.Take(3), s => Assert.False(s!.Busy));
        Assert.True(sessions[3]!.Busy);
    }

    [Fact]
    public async Task Budget_frees_up_after_a_minute()
    {
        var api = new SlowApi();
        var swap = NewSwap(api, perMinute: 1);
        var now = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        swap.Clock = () => now;

        Assert.False(swap.Start("xmr", "btc", 1m)!.Busy);
        Assert.True(swap.Start("xmr", "btc", 2m)!.Busy);

        now = now.AddSeconds(61);
        var later = swap.Start("xmr", "btc", 2m)!;
        Assert.False(later.Busy);
        await foreach (var _ in later.ReadAsync()) { }
    }

    [Fact]
    public async Task Same_request_shares_one_fan_out_and_does_not_use_more_budget()
    {
        var api = new SlowApi();
        var swap = NewSwap(api, perMinute: 1);

        var first = swap.Start("xmr", "btc", 1m)!;
        var second = swap.Start("xmr", "btc", 1.05m)!; // snaps to the same step
        Assert.False(first.Busy);
        Assert.False(second.Busy);

        var a = new List<SwapQuoteRow>();
        await foreach (var r in first.ReadAsync()) { a.Add(r); }
        var b = new List<SwapQuoteRow>();
        await foreach (var r in second.ReadAsync()) { b.Add(r); }

        Assert.Equal(1, api.PriceCalls);
        Assert.Equal(0.004m, Assert.Single(a).Receive);
        // The shared per-unit rate is applied to each visitor's exact amount.
        Assert.Equal(1.05m * 0.004m, Assert.Single(b).Receive);
        // A non-web link from configuration is never offered as the "Swap" link.
        Assert.Null(a[0].OutHref);
    }

    [Fact]
    public async Task Disconnected_client_still_leaves_a_cached_result()
    {
        var api = new SlowApi { Delay = TimeSpan.FromMilliseconds(200) };
        var swap = NewSwap(api, perMinute: 1);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in swap.Start("xmr", "btc", 1m)!.ReadAsync(cts.Token)) { }
            });
        }

        // The run keeps going without the client and caches its result; a second request
        // (with the budget already spent) is answered from it, not refused as busy.
        await Task.Delay(500);
        var again = swap.Start("xmr", "btc", 1m)!;
        Assert.False(again.Busy);
        var rows = new List<SwapQuoteRow>();
        await foreach (var r in again.ReadAsync()) { rows.Add(r); }

        Assert.Single(rows);
        Assert.Equal(1, api.PriceCalls);
    }

    [Theory]
    [InlineData("https://example.com/x", true)]
    [InlineData("http://example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("//example.com", false)]
    [InlineData("", false)]
    public void Sponsor_links_must_be_web_links(string link, bool ok)
        => Assert.Equal(ok, SwapQuoteService.IsWebLink(link));
}
