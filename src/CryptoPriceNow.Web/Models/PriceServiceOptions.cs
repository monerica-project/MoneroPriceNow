namespace CryptoPriceNow.Web.Models;

public sealed class PriceServiceOptions
{
    public int PriceCacheSeconds { get; set; } = 15;
    public int CurrenciesCacheMinutes { get; set; } = 60;

    // Exchange keys to exclude from the price tables entirely. Use for exchanges
    // that can't quote BOTH buy and sell for XMR (this is a two-way price site) —
    // e.g. ChangeHero only lets you SELL Monero, never buy it.
    public string[] ExcludedExchanges { get; set; } = [];
}