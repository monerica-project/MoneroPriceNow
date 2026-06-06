namespace ExchangeServices.Models;

public sealed class ZeroTraceOptions
{
    public string SiteUrl { get; set; } = "https://0trace.io";
    public string SiteName { get; set; } = "0trace";
    public string BaseUrl { get; set; } = "https://0trace.io/api";

    /// <summary>
    /// Partner API key. Optional — /v1/currencies and /v1/price both accept
    /// unsigned anonymous calls and return the base rate when no creds are
    /// configured. Provide ApiKey + ApiSecret to receive partner-specific
    /// quotes (and optional afftax markup).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Partner API secret. Used as the HMAC-SHA256 key over the literal
    /// request body bytes. Shown only once at issuance.
    /// </summary>
    public string? ApiSecret { get; set; }

    /// <summary>
    /// Optional partner markup in basis points (0..2000 = 0–20%).
    /// Leave 0 for the unmarked base rate that price-aggregator UIs typically
    /// want to display. Ignored on anonymous calls.
    /// </summary>
    public int Afftax { get; set; } = 0;

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 2;

    /// <summary>
    /// How long to cache the currency list (seconds).
    /// /v1/currencies has its own 30s server-side cache, so don't go lower.
    /// </summary>
    public int CurrenciesCacheSeconds { get; set; } = 21600;

    public string? UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}
