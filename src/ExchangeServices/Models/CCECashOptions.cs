namespace ExchangeServices.Models;

public sealed class CCECashOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://cce.cash";

    // Optional (only needed if you call endpoints that require signing)
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 10;
    public char PrivacyLevel { get; set; }
}