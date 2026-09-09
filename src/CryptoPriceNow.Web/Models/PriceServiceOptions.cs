namespace CryptoPriceNow.Web.Models;

public sealed class PriceServiceOptions
{
    public int PriceCacheSeconds { get; set; } = 15;
    public int CurrenciesCacheMinutes { get; set; } = 60;

    // Every quote is sized to this USD trade value so all exchanges (and both the buy and
    // sell sides) are compared at the same, realistic trade size. Small trades quote a worse
    // effective rate because fixed network/withdrawal fees weigh more heavily; the rate flattens
    // out by a few thousand dollars, so a mid-single-thousands figure is representative.
    public decimal TargetTradeUsd { get; set; } = 2500m;

    // Used to size the XMR sell probe when the live XMR/USD price can't be fetched — roughly
    // TargetTradeUsd worth of XMR at a typical price. Keeps quoting working if the price feed is down.
    public decimal FallbackSellProbeXmr { get; set; } = 7.5m;

    // How long a fetched USD spot price (used only to size probes) is cached.
    public int UsdPriceCacheMinutes { get; set; } = 5;

    // Exchange keys to exclude from the price tables entirely. Use for exchanges
    // that can't quote BOTH buy and sell for XMR (this is a two-way price site) —
    // e.g. ChangeHero only lets you SELL Monero, never buy it.
    public string[] ExcludedExchanges { get; set; } = [];

    // Sanity cap on the buy/sell spread. A row whose spread ((Buy-Sell)/Buy) exceeds this
    // is hidden as nonsensical bad data — real XMR spreads top out around 10-13%, so a 70%+
    // spread means one side (usually a bad/mis-scaled buy quote) is garbage, not a real price.
    // Paired with the negative-spread hide (Buy < Sell) so only sane spreads [0, max] show.
    public decimal MaxSpreadFraction { get; set; } = 0.25m;
}