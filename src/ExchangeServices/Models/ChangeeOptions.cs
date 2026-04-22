namespace ExchangeServices.Implementations;

public sealed class ChangeeOptions
{
    public string  BaseUrl               { get; set; } = "https://changee.com";
    public string  ApiKey                { get; set; } = "";
    public string  SiteName              { get; set; } = "Changee";
    public string? SiteUrl               { get; set; } = "https://changee.com";
    public int     RequestTimeoutSeconds { get; set; } = 10;
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}
