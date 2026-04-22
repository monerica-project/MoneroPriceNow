namespace ExchangeServices.Models;

public sealed class ExolixOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://exolix.com/api/v2";

    // Optional for most endpoints, required for affiliate/account features
    public string? ApiKey { get; set; }

    // "fixed" (default per docs) or "float"
    public string RateType { get; set; } = "fixed";

    public int TimeoutSeconds { get; set; } = 15;
    public int RetryCount { get; set; } = 1;

    // Currencies paging
    public int CurrenciesPageSize { get; set; } = 200;

    public string? UserAgent { get; set; }
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}