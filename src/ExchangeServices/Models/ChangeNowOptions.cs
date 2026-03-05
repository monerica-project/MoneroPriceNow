namespace ExchangeServices.Models;

public sealed class ChangeNowOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.changenow.io";
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// "standard" or "fixed-rate"
    /// </summary>
    public string Flow { get; set; } = "standard";

    /// <summary>
    /// How long to cache currency list (seconds)
    /// </summary>
    public int CurrenciesCacheSeconds { get; set; } = 6 * 60 * 60; // 6 hours

    /// <summary>
    /// Request timeout enforced by our wrapper (seconds)
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 20;

    /// <summary>
    /// Retry count for transient failures (0..6 typical)
    /// </summary>
    public int RetryCount { get; set; } = 2;

    public string? UserAgent { get; set; } = "ExchangeServices/1.0";
}