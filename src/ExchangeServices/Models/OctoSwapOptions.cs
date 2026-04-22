namespace ExchangeServices.Models;

public sealed class OctoSwapOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";

    // Header: api-key
    public string ApiKey { get; set; } = "";
    public char PrivacyLevel { get; set; }
    public decimal BuyProbeAmountUsdt { get; set; } = 10_000m;
    public decimal MinAmountUsd { get; set; }
}