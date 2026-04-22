namespace ExchangeServices.Implementations;

public sealed class ChangeHeroOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;  // required for HMAC-SHA512 sign header
    public string BaseUrl { get; set; } = "https://api.changehero.io/v2/";
    public string SiteName { get; set; } = "ChangeHero";
    public string? SiteUrl { get; set; } = "https://changehero.io";
    public char PrivacyLevel { get; set; } = 'C';
    public int RequestTimeoutSeconds { get; set; } = 10;

    public string XmrCode { get; set; } = "xmr";
    public string UsdtCode { get; set; } = "usdttrc20";
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }
}