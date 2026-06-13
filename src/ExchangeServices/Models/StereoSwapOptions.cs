namespace ExchangeServices.Implementations;

public sealed class StereoSwapOptions
{
    public string ApiKey { get; set; } = string.Empty;

    // Auth header construction. Header value is "{AuthScheme} {ApiKey}", or just
    // "{ApiKey}" when AuthScheme is blank. Confirmed via StereoSwap support + Swagger
    // ApiKeyAuth: header name "X-API-Key", raw key value, NO scheme prefix.
    //   X-API-Key: <key>
    public string AuthHeaderName { get; set; } = "X-API-Key";
    public string AuthScheme { get; set; } = "";

    public string BaseUrl { get; set; } = "https://api.stereoswap.app";
    public string SiteName { get; set; } = "StereoSwap";
    public string? SiteUrl { get; set; } = "https://stereoswap.app";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;

    // type_swap: 2 = floating rate, 1 = fixed rate
    public int TypeSwap { get; set; } = 2;
    // mode: valid values are "standard", "oblivion", "lets_refresh" (per API validation).
    public string Mode { get; set; } = "standard";

    public string XmrCoin { get; set; } = "XMR";
    public string XmrNetwork { get; set; } = "XMR";
    public string UsdtCoin { get; set; } = "USDT";
    // NOTE: StereoSwap does NOT support XMR<->USDT on TRX (TRC20) — "Such exchange pair
    // is not available". It DOES support XMR<->USDT on ETH/BSC/MATIC/ARBITRUM/APT/etc.
    // USDT price is the same across networks, so ETH is used as the quote leg.
    public string UsdtNetwork { get; set; } = "ETH";
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }
}