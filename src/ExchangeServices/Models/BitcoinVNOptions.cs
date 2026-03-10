namespace ExchangeServices.Implementations;

public sealed class BitcoinVNOptions
{
    public string  BaseUrl               { get; set; } = "https://bitcoinvn.io";
    public string? ApiKey                { get; set; } // X-API-KEY — not required for price endpoints
    public string  SiteName              { get; set; } = "BitcoinVN";
    public string? SiteUrl               { get; set; } = "https://bitcoinvn.io";
    public int     RequestTimeoutSeconds { get; set; } = 10;
    public char PrivacyLevel { get; set; }
}
