namespace ExchangeServices.Implementations;

public sealed class StereoSwapOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.stereoswap.app";
    public string SiteName { get; set; } = "StereoSwap";
    public string? SiteUrl { get; set; } = "https://stereoswap.app";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;

    // type_swap: 2 = floating rate, 1 = fixed rate
    public int TypeSwap { get; set; } = 2;
    // mode: "standard" (check docs for other modes e.g. "fixed")
    public string Mode { get; set; } = "standard";

    public string XmrCoin { get; set; } = "XMR";
    public string XmrNetwork { get; set; } = "XMR";
    public string UsdtCoin { get; set; } = "USDT";
    public string UsdtNetwork { get; set; } = "TRX";
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }
}