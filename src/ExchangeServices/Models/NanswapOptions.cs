namespace ExchangeServices.Models;

public sealed class NanswapOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.nanswap.com";
    public int TimeoutSeconds { get; set; } = 12;
    public string? UserAgent { get; set; } = "CryptoPriceNow/1.0";

    // Not required for estimates (only create-order needs it), but keep for future.
    public string? ApiKey { get; set; }
}