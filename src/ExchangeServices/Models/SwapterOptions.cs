namespace ExchangeServices.Implementations;

public sealed class SwapterOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.swapter.io/";
    public string SiteName { get; set; } = "Swapter";
    public string? SiteUrl { get; set; } = "https://swapter.io";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;

    public string XmrCoin { get; set; } = "XMR";
    public string XmrNetwork { get; set; } = "XMR";
    public string UsdtCoin { get; set; } = "USDT";
    public string UsdtNetwork { get; set; } = "TRX";   // Swapter uses chain ticker, not protocol name
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }
}