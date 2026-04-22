namespace ExchangeServices.Implementations;

public sealed class GoDexOptions
{
    public string  BaseUrl               { get; set; } = "https://api.godex.io";
    public string? ApiKey                { get; set; } // public-key header — required for affiliate tracking
    public string? AffiliateId          { get; set; } // affiliate_id passed on transaction creation
    public string  SiteName              { get; set; } = "GoDex";
    public string? SiteUrl               { get; set; } = "https://godex.io";
    public int     RequestTimeoutSeconds { get; set; } = 12;
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}
