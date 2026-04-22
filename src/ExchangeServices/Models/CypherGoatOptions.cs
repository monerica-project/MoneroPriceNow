namespace ExchangeServices.Implementations;

public sealed class CypherGoatOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.cyphergoat.com";
    public string SiteName { get; set; } = "CypherGoat";
    public string? SiteUrl { get; set; } = "https://cyphergoat.com";
    public char PrivacyLevel { get; set; } = 'A';  // aggregator focused on privacy
    public int RequestTimeoutSeconds { get; set; } = 10;

    // Coin/network identifiers (lowercase per API examples)
    // network2 for USDT TRC20: try "trx", "tron", or "trc20" — whichever returns a rate
    public string XmrCoin { get; set; } = "xmr";
    public string XmrNetwork { get; set; } = "xmr";
    public string UsdtCoin { get; set; } = "usdt";
    public string UsdtNetwork { get; set; } = "trx";
    public decimal BuyProbeAmountUsdt { get; set; } = 200m;  // raised — 100 USDT may be below
    public decimal MinAmountUsd { get; set; }
}
