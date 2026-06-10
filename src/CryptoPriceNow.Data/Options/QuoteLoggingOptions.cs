namespace CryptoPriceNow.Data.Options;

public sealed class QuoteLoggingOptions
{
    /// <summary>Master switch. False = no DB writes even if a connection string exists.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minimum seconds between logged snapshots per pair. The warmer runs every
    /// 15s; 30 here halves write volume while matching the chart's live bucket.
    /// </summary>
    public int LogIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Skip inserting a row when the exchange's quote timestamp hasn't changed
    /// since the last insert (the per-exchange memory cache returned the same
    /// quote). Keeps the table honest: one row per actual exchange quote.
    /// Set false to log every snapshot regardless.
    /// </summary>
    public bool DedupeByQuoteTimestamp { get; set; } = true;

    /// <summary>Delete quotes older than this many days. 0 = keep forever.</summary>
    public int RetentionDays { get; set; } = 0;

    /// <summary>How often exchange metadata (name/url/privacy/LastSeen) is refreshed in the DB.</summary>
    public int MetadataRefreshMinutes { get; set; } = 10;
}
