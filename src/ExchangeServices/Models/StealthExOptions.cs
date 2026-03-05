namespace ExchangeServices.Models;

public sealed class StealthExOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://api.stealthex.io";
    public string? ApiKey { get; set; }
    public string? UserAgent { get; set; } = "CryptoPriceNow/1.0";

    // Short for price calls
    public int TimeoutSeconds { get; set; } = 10;

    // Longer for /currencies pagination
    public int CurrenciesTimeoutSeconds { get; set; } = 45;

    // Smaller page reduces response size
    public int CurrenciesPageSize { get; set; } = 100;

    // Optional: retry once on timeout
    public int CurrenciesTimeoutRetries { get; set; } = 1;
}