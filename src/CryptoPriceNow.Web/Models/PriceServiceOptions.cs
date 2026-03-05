namespace CryptoPriceNow.Web.Models;

public sealed class PriceServiceOptions
{
    public int PriceCacheSeconds { get; set; } = 15;
    public int CurrenciesCacheMinutes { get; set; } = 60;
}