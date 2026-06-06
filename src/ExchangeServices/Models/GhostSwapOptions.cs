namespace ExchangeServices.Models;

public sealed class GhostSwapOptions
{
    public string SiteUrl { get; set; } = "https://ghostswap.io";
    public string SiteName { get; set; } = "GhostSwap";
    public string BaseUrl { get; set; } = "https://partners-api.ghostswap.io";

    /// <summary>
    /// Partner public key — looks like "gspk_live_...".
    /// Always recoverable from the GhostSwap partner dashboard.
    /// </summary>
    public string PublicKey { get; set; } = "";

    /// <summary>
    /// Partner secret — looks like "gssk_live_...".
    /// Shown once at creation; re-viewable via the dashboard's "Reveal secret" button.
    /// </summary>
    public string Secret { get; set; } = "";

    /// <summary>
    /// Reference USDT amount used when fetching buy quotes (USDT → XMR).
    /// buyPrice = BuyReferenceAmountUsdt / amountUserReceives.
    /// A larger value avoids sub-minimum rejections.
    /// </summary>
    public decimal BuyReferenceAmountUsdt { get; set; } = 100m;

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 2;

    /// <summary>How long to cache the currency list (seconds).</summary>
    public int CurrenciesCacheSeconds { get; set; } = 21600;

    public string? UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}
