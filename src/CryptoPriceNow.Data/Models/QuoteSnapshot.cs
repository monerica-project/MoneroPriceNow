namespace CryptoPriceNow.Data.Models;

/// <summary>One exchange's quote inside a snapshot. Mirrors TwoWayPriceRow plus rate type.</summary>
public sealed record QuoteRowDto(
    string ExchangeKey,
    string SiteName,
    string? SiteUrl,
    char? PrivacyLevel,
    string RateType,            // "float" | "fixed"
    decimal? Buy,
    decimal? Sell,
    DateTimeOffset? QuoteTsUtc  // the exchange's own fetch timestamp
);

/// <summary>
/// Everything captured in one warm cycle for one trading pair.
/// Handed off to IPriceQuoteSink fire-and-forget so the hot path never
/// waits on the database.
/// </summary>
public sealed record QuoteSnapshot(
    string Pair,                    // "XMR/USDT:Tron"
    DateTimeOffset CapturedUtc,
    IReadOnlyList<QuoteRowDto> Rows
);
