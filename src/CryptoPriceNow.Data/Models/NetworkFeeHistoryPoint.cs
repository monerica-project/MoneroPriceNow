namespace CryptoPriceNow.Data.Models;

/// <summary>One time bucket of aggregated network-fee samples.</summary>
public sealed record NetworkFeeHistoryPoint(
    DateTimeOffset BucketUtc,
    decimal? AvgUsdPerTx,
    decimal? AvgNative,
    int Samples
);

public sealed record NetworkFeeHistoryResult(
    string Network,
    string RangeKey,
    int BucketSeconds,
    IReadOnlyList<NetworkFeeHistoryPoint> Points,
    DateTimeOffset? OldestUtc
);
