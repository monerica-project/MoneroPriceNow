namespace ExchangeServices.Models;

public sealed class LetsExchangeOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.letsexchange.io/api";
    public string ApiKey { get; set; } = "";          // bearer token value
    public string AffiliateId { get; set; } = "";     // required by /v1/info
    public bool UseFloatRate { get; set; } = true;

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 2;

    public string? UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}