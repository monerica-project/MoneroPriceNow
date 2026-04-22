namespace ExchangeServices.SageSwap;

public sealed class SageSwapOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://sageswap.io/api"; // base includes /api
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public string? Token { get; set; }
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}