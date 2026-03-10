namespace CryptoPriceNow.Web.Models;

public sealed record TwoWayPriceRow(
    string Exchange,          // internal key, e.g. "fixedfloat"
    string SiteName,          // display name, e.g. "FixedFloat"
    string? SiteUrl,          // affiliate link, e.g. "https://ff.io?ref=..."
    decimal? Sell,
    decimal? Buy,
    DateTimeOffset? TsUtc,
    char? PrivacyLevel
);
