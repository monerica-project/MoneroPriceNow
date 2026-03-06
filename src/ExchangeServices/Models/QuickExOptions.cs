namespace ExchangeServices.Implementations;

public sealed class QuickExOptions
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string  SiteName  { get; set; } = "QuickEx";
    public string? SiteUrl   { get; set; } = "https://quickex.io";
    public string  BaseUrl   { get; set; } = "https://quickex.io";

    // ── API credentials (required for v2 HMAC; leave blank to use public v1) ──
    // Obtain from: https://quickex.io  →  Account  →  API
    public string? ApiPublicKey  { get; set; }
    public string? ApiPrivateKey { get; set; }

    // ── Rate type ─────────────────────────────────────────────────────────────
    // "FLOATING" (default) or "FIXED"
    public string RateType { get; set; } = "FLOATING";

    // ── HTTP settings ─────────────────────────────────────────────────────────
    public double RequestTimeoutSeconds { get; set; } = 12;
    public int    RetryCount            { get; set; } = 2;

    // ── Cache TTLs (seconds) ─────────────────────────────────────────────────
    public int PairsCacheSeconds { get; set; } = 300;  // instrument list
    public int QuoteCacheSeconds { get; set; } = 12;   // rate quotes

    // ── Optional User-Agent ───────────────────────────────────────────────────
    public string? UserAgent { get; set; }
}
