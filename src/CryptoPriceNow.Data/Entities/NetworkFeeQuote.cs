namespace CryptoPriceNow.Data.Entities;

/// <summary>
/// One logged on-chain fee sample for a network, used to chart fees over time.
/// Stores the representative ("Normal") tier: a native rate plus the estimated
/// cost of a typical transaction in USD.
/// </summary>
public sealed class NetworkFeeQuote
{
    public long Id { get; set; }

    /// <summary>"bitcoin" | "ethereum" | "monero".</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>Native headline rate (sat/vB, Gwei, or piconero/byte).</summary>
    public decimal Native { get; set; }

    /// <summary>Unit for <see cref="Native"/> — "sat/vB", "Gwei", "pXMR/byte".</summary>
    public string NativeUnit { get; set; } = string.Empty;

    /// <summary>Estimated cost of a typical transaction in USD (null if price unknown).</summary>
    public decimal? UsdPerTx { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }
}
