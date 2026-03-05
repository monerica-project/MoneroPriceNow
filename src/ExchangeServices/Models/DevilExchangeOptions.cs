namespace ExchangeServices.Models;

public sealed class DevilExchangeOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://devil.exchange";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";

    // floating|fixed
    public string DefaultRateType { get; set; } = "floating";
    public string? RateType { get;  set; }
    public int PairsCacheSeconds { get;  set; }
    public int RequestTimeoutSeconds { get;  set; }
    public int RetryCount { get;  set; }
    public int QuoteCacheSeconds { get;  set; }
}