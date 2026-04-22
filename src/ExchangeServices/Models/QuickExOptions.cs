namespace ExchangeServices.Implementations;

public sealed class QuickexOptions
{
    public string BaseUrl { get; set; } = "https://quickex.io/";
    public string SiteName { get; set; } = "Quickex";
    public string? SiteUrl { get; set; } = "https://quickex.io";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;
    public string? ReferrerId { get; set; }  // e.g. "aff_your-id"

    public string XmrCurrency { get; set; } = "XMR";
    public string XmrNetwork { get; set; } = "XMR";
    public string UsdtCurrency { get; set; } = "USDT";
    public string UsdtNetwork { get; set; } = "TRC20";

    // Probe amounts — bumped automatically if below API minimum via 422 retry
    public decimal SellProbeAmountXmr { get; set; } = 2m;
    public decimal BuyProbeAmountUsdt { get; set; } = 200m;
    public decimal MinAmountUsd { get; set; }
}