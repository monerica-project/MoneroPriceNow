namespace CryptoPriceNow.Data.Entities;

/// <summary>
/// Registry of every exchange client the site has ever produced a quote for.
/// Rows are inserted automatically the first time an exchange key is seen,
/// so integrating a new client requires zero database work — it self-registers
/// on the first warm cycle and quotes log from that point forward.
/// </summary>
public sealed class Exchange
{
    public int Id { get; set; }

    /// <summary>Internal key from IExchangePriceApi.ExchangeKey (e.g. "fixedfloat"). Unique.</summary>
    public string ExchangeKey { get; set; } = string.Empty;

    /// <summary>Display name (e.g. "FixedFloat").</summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>Affiliate/referral URL, null if none.</summary>
    public string? SiteUrl { get; set; }

    /// <summary>Privacy grade ("A", "B", "C", "V"), null if the client doesn't report one.</summary>
    public string? PrivacyLevel { get; set; }

    /// <summary>"float" or "fixed" — from IRateType, defaults to "float" (instant-swap norm).</summary>
    public string RateType { get; set; } = "float";

    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>Last time a warm cycle included this exchange. Stale = client removed/disabled.</summary>
    public DateTimeOffset LastSeenUtc { get; set; }

    public bool IsActive { get; set; } = true;

    public ICollection<PriceQuote> Quotes { get; set; } = new List<PriceQuote>();
}
