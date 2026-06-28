namespace CryptoPriceNow.Data.Options;

/// <summary>Config for logging network-fee samples over time. Bound from "FeeLogging".</summary>
public sealed class FeeLoggingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum gap between logged samples per network (seconds).</summary>
    public int LogIntervalSeconds { get; set; } = 60;

    /// <summary>Delete samples older than this many days. 0 = keep forever.</summary>
    public int RetentionDays { get; set; } = 0;
}
