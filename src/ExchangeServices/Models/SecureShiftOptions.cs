namespace ExchangeServices.Implementations;

public sealed class SecureShiftOptions
{
    public string  ApiKey                { get; set; } = string.Empty;
    public string  BaseUrl               { get; set; } = "https://secureshift.io/api/v3/";
    public string  SiteName              { get; set; } = "SecureShift";
    public string? SiteUrl               { get; set; } = "https://secureshift.io";
    public char    PrivacyLevel          { get; set; } = 'B';
    public int     RequestTimeoutSeconds { get; set; } = 10;

    // Currency identifiers as used by SecureShift API
    public string XmrSymbol   { get; set; } = "xmr";
    public string XmrNetwork  { get; set; } = "xmr";
    public string UsdtSymbol  { get; set; } = "usdt";
    public string UsdtNetwork { get; set; } = "trc20";

    // Probe amount for buy price (must exceed SecureShift minimum)
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
}
