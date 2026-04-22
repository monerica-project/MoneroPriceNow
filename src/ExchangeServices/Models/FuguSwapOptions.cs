namespace ExchangeServices.Models;

public sealed class FuguSwapOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.fuguswap.com/partners";
    public int TimeoutSeconds { get; set; } = 20;

    // Required
    public string ApiKey { get; set; } = "";
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}