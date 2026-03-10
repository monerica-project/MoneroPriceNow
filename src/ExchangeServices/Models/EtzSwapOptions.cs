namespace ExchangeServices.Models;

public sealed class EtzSwapOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.etz-swap.com";
    public int TimeoutSeconds { get; set; } = 20;

    // Optional: only needed if you want account-specific rates
    public string? ApiKey { get; set; }
    public string? ApiSecretKey { get; set; }
    public string? ApiKeyVersion { get; set; } = "1";
    public char PrivacyLevel { get; set; }
}