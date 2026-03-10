namespace ExchangeServices.Models;

public sealed class WizardSwapOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://www.wizardswap.io";

    /// <summary>
    /// Optional. If provided, WizardSwap attributes swaps to you (0.5%).
    /// </summary>
    public string? ApiKey { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 20;
    public int RetryCount { get; set; } = 2;

    public int CurrenciesCacheSeconds { get; set; } = 6 * 60 * 60; // 6 hours
    public string? UserAgent { get; set; } = "ExchangeServices/1.0";
    public char PrivacyLevel { get; set; }
}