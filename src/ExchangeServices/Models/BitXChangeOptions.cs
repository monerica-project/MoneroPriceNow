namespace ExchangeServices.Implementations;

public sealed class BitXChangeOptions
{
    public string  ApiKey                { get; set; } = string.Empty;
    public string  BaseUrl               { get; set; } = "https://api.bitxchange.io";
    public string  SiteName              { get; set; } = "BitXChange";
    public string? SiteUrl               { get; set; } = "https://bitxchange.io";
    public char    PrivacyLevel          { get; set; } = 'B';
    public int     RequestTimeoutSeconds { get; set; } = 10;

    public string  XmrSymbol         { get; set; } = "XMR";
    public string  XmrNetwork        { get; set; } = "XMR";
    public string  UsdtSymbol        { get; set; } = "USDT";
    public string  UsdtNetwork       { get; set; } = "TRC20";
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }
}
