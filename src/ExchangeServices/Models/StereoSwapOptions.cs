namespace ExchangeServices.Implementations;

public sealed class StereoSwapOptions
{
    public string ApiKey { get; set; } = string.Empty;

    // Auth header construction. The header value is "{AuthScheme} {ApiKey}", or just
    // "{ApiKey}" when AuthScheme is blank. Set these to match StereoSwap's Swagger
    // "Authorize" dialog without recompiling. Common cases:
    //   Authorization + "Bearer"  → Authorization: Bearer <key>
    //   Authorization + ""        → Authorization: <key>      (raw key, no prefix)
    //   X-API-Key     + ""        → X-API-Key: <key>
    public string AuthHeaderName { get; set; } = "Authorization";
    public string AuthScheme { get; set; } = "Bearer";

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