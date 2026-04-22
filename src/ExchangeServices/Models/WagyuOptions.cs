 
namespace ExchangeServices.Implementations;

public sealed class WagyuOptions
{
    public string SiteName { get; set; } = "Wagyu";
    public string? SiteUrl { get; set; } = "https://wagyu.xyz";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public char PrivacyLevel { get; set; } = 'A';
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int RatesCacheSeconds { get; set; } = 30;
    public int RetryCount { get; set; } = 2;
    public string? UserAgent { get; set; }
    public decimal MinAmountUsd { get; set; }
}