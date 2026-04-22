namespace ExchangeServices.Implementations;

public sealed class SwapgateOptions
{
    public string BaseUrl { get; set; } = "https://swapgate.io/";
    public string SiteName { get; set; } = "Swapgate";
    public string? SiteUrl { get; set; } = "https://swapgate.io";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;

    public string XmrCurrency { get; set; } = "XMR";
    public string XmrNetwork { get; set; } = "XMR";
    public string UsdtCurrency { get; set; } = "USDT";
    public string UsdtNetwork { get; set; } = "TRC20";
    public decimal SellProbeAmountXmr { get; set; } = 2m;    // must exceed Swapgate min (~1.566 XMR)
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }
}