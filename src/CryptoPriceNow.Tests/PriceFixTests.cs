using CryptoPriceNow.Data.Interfaces;
using CryptoPriceNow.Data.Services;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using CryptoPriceNow.Web.Support;
using ExchangeServices.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace CryptoPriceNow.Tests;

public class PriceFixTests
{
    // ── Fakes ──────────────────────────────────────────────────────────────────
    private sealed class CountingApi : IExchangePriceApi, IExchangeCurrencyApi
    {
        public int PriceCalls;
        public int CurrencyCalls;
        public Func<int, IReadOnlyList<ExchangeCurrency>>? Currencies;

        public string ExchangeKey => "fake";
        public string SiteName => "Fake";
        public string? SiteUrl => null;

        public Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PriceCalls);
            return Task.FromResult<PriceResult?>(new PriceResult("fake", query.Base, query.Quote, 1m, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref CurrencyCalls);
            return Task.FromResult(Currencies?.Invoke(n) ?? (IReadOnlyList<ExchangeCurrency>)Array.Empty<ExchangeCurrency>());
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }

    private static PriceService NewPriceService(CountingApi api) => new(
        new IExchangePriceApi[] { api },
        new IExchangeCurrencyApi[] { api },
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new PriceServiceOptions()),
        new NullPriceQuoteSink(),
        new NoHttp());

    private static TwoWayPriceRow Row(string ex, decimal? buy, decimal? sell, string rateType = "float") =>
        new(ex, ex, null, sell, buy, DateTimeOffset.UtcNow, 'A', null, rateType);

    // ── Item 9: pair allow-list ───────────────────────────────────────────────
    [Theory]
    [InlineData("XMR", "USDTTRC", true)]
    [InlineData("xmr", "usdttrc", true)]
    [InlineData("XMR", "USDT:Tron", true)]
    [InlineData("XMR", "BTC", true)]
    [InlineData("XMR", "ETH", true)]
    [InlineData("XMR", "DOGE", false)]
    [InlineData("BTC", "XMR", false)]
    [InlineData("XMR", "USDTERC", false)]
    [InlineData("", "BTC", false)]
    [InlineData("XMR", null, false)]
    public void IsSupportedPair_OnlyCatalogPairs(string? b, string? q, bool expected)
        => Assert.Equal(expected, PriceService.IsSupportedPair(b, q));

    [Fact]
    public async Task UnsupportedPair_NeverFansOutToExchanges()
    {
        var api = new CountingApi();
        var svc = NewPriceService(api);

        Assert.Empty(await svc.GetPricesAsync("XMR", "DOGE"));
        Assert.Empty(await svc.GetTwoWayPricesAsync("SHIB", "PEPE"));
        Assert.Empty(await svc.GetSellRowsAsync("XMR", "LTC"));

        Assert.Equal(0, api.PriceCalls);
        Assert.Equal(0, api.CurrencyCalls);
    }

    // ── Item 2: public/NoJs mid = board Float-view mid ────────────────────────
    [Fact]
    public void FloatMid_IgnoresFixedRows()
    {
        var rows = new[]
        {
            Row("a", buy: 102m, sell: 98m),
            Row("b", buy: 104m, sell: 100m),
            Row("a", buy: 150m, sell: 90m, rateType: "fixed"),
        };

        Assert.Equal((102m + 98m + 104m + 100m) / 4m, BoardMid.FloatMid(rows));
    }

    [Fact]
    public void FloatMid_TreatsBlankRateTypeAsFloat_AndAppliesMinValidPrice()
    {
        var rows = new[]
        {
            Row("a", buy: 102m, sell: 98m, rateType: ""),
            Row("junk", buy: 0.5m, sell: 0.4m),
        };

        Assert.Equal(100m, BoardMid.FloatMid(rows, minValidPrice: 1m));
        Assert.Null(BoardMid.FloatMid(Array.Empty<TwoWayPriceRow>()));
        Assert.Null(BoardMid.FloatMid(new[] { Row("f", 1m, 1m, "fixed") }));
    }

    // ── Item 6: dedupe key distinguishes float and fixed rows ────────────────
    [Fact]
    public void QuoteLogger_DedupeKey_IncludesRateType()
    {
        var f = PriceQuoteLogger.DedupeKey("changenow", "XMR/BTC", "float");
        var x = PriceQuoteLogger.DedupeKey("changenow", "XMR/BTC", "fixed");
        Assert.NotEqual(f, x);
        Assert.Equal(f, PriceQuoteLogger.DedupeKey("ChangeNow", "XMR/BTC", null));
    }

    // ── Item 4: swap currency list — failures are not cached for an hour ─────
    private static SwapQuoteService NewSwap(CountingApi api) => new(
        new IExchangePriceApi[] { api },
        new IExchangeCurrencyApi[] { api },
        new MemoryCache(new MemoryCacheOptions()),
        new NoHttp(),
        new ConfigurationBuilder().Build());

    [Fact]
    public async Task SwapCurrencies_ExceptionIsNotCached()
    {
        var api = new CountingApi
        {
            Currencies = n => n == 1
                ? throw new HttpRequestException("boom")
                : new[] { new ExchangeCurrency("xmr", "XMR", "Mainnet") },
        };
        var swap = NewSwap(api);

        Assert.Empty(await swap.GetCurrenciesAsync("fake", CancellationToken.None));
        Assert.Single(await swap.GetCurrenciesAsync("fake", CancellationToken.None)); // retried
        Assert.Single(await swap.GetCurrenciesAsync("fake", CancellationToken.None)); // now cached
        Assert.Equal(2, api.CurrencyCalls);
    }

    [Fact]
    public async Task SwapCurrencies_CallerCancellationPropagates()
    {
        var api = new CountingApi();
        var swap = NewSwap(api);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => swap.GetCurrenciesAsync("fake", cts.Token));
    }

    // ── Low: exchange directory without a DB answers "nothing" instead of 500 ─
    [Fact]
    public async Task ExchangeDirectory_NoDatabase_ReturnsEmpty()
    {
        var dir = new ExchangeDirectoryService(new ConfigurationBuilder().Build());

        Assert.Empty(await dir.GetActiveAsync());
        Assert.Null(await dir.GetBySlugAsync("changenow"));
        Assert.Empty(await dir.GetQuotedPairsAsync(1));
    }

    [Fact]
    public void SaneQuotePredicate_MatchesRollupThreshold()
        => Assert.Contains("<= 0.25", PriceHistoryService.SaneQuotePredicate);
}
