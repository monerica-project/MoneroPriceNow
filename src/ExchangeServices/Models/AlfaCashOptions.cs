namespace ExchangeServices.Implementations;

public sealed class AlfaCashOptions
{
    public string  BaseUrl               { get; set; } = "https://www.alfa.cash";
    public string  SiteName              { get; set; } = "AlfaCash";
    public string? SiteUrl               { get; set; } = "https://www.alfa.cash";
    public int     RequestTimeoutSeconds { get; set; } = 12;

    public char PrivacyLevel { get; set; }

    public decimal MinAmountUsd { get; set; }
}
