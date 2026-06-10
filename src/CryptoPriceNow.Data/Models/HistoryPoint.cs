namespace CryptoPriceNow.Data.Models;

/// <summary>One time bucket of aggregated prices across all exchanges.</summary>
public sealed record HistoryPoint(
    DateTimeOffset BucketUtc,
    decimal? AvgBuy,
    decimal? AvgSell,
    decimal? Market,    // midpoint of avg buy/sell (or whichever side exists)
    int Samples
);

public sealed record HistoryResult(
    string Pair,
    string RangeKey,
    int BucketSeconds,
    IReadOnlyList<HistoryPoint> Points,
    DateTimeOffset? OldestUtc   // earliest quote for this pair (for range availability)
);
