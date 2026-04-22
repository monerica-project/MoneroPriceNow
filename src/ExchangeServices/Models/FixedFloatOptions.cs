namespace ExchangeServices.Models;

public sealed class FixedFloatOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://ff.io";
    public int TimeoutSeconds { get; set; } = 20;

    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}
