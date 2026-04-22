namespace ExchangeServices.Implementations;

public sealed class XgramOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string SiteName { get; set; } = "Xgram";
    public string? SiteUrl { get; set; } = "https://xgram.io";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;

    // Currency codes as returned by /list-currency-options
    public string XmrCode { get; set; } = "XMR";
    public string UsdtCode { get; set; } = "USDTTRC20";
    public string? BaseUrl { get; set; }

    // Amount of USDT used when probing the buy rate (must exceed Xgram's minFrom).
    // The actual buyPrice is derived by inverting the rate, so the amount cancels out.
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }
}