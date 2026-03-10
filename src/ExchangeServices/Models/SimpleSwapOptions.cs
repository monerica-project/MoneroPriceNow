namespace ExchangeServices.Models;

public sealed class SimpleSwapOptions
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.simpleswap.io";
    public string SiteName { get; set; } = "SimpleSwap";
    public string? SiteUrl { get; set; } = "https://simpleswap.io";
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public char PrivacyLevel { get; set; }
}
