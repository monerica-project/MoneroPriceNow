namespace ExchangeServices.Implementations;

public sealed class WagyuOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.wagyu.xyz";
    public string? UserAgent { get; set; }

    // rates.xml cache-control is max-age=30, so 20-30s is plenty
    public int RatesCacheSeconds { get; set; } = 25;

    // per-attempt timeout
    public int RequestTimeoutSeconds { get; set; } = 8;

    // retries AFTER first attempt (0 = only one attempt)
    public int RetryCount { get; set; } = 2;
    public char PrivacyLevel { get; set; }
}