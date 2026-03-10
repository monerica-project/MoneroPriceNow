namespace ExchangeServices.Implementations;

public sealed class CypherGoatOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.cyphergoat.com";
    public string ApiKey { get; set; } = "";

    // SafeHttp timeout for each request
    public int RequestTimeoutSeconds { get; set; } = 12;

    // retry count for transient failures (timeouts/429/5xx)
    public int RetryCount { get; set; } = 3;

    public string? UserAgent { get; set; } = null;
    public char PrivacyLevel { get; set; }
}