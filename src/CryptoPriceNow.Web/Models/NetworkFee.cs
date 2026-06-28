namespace CryptoPriceNow.Web.Models;

/// <summary>One priority level of a network fee (e.g. "Fast · ~10 min").</summary>
/// <param name="Label">Priority name shown to the user (Fast / Normal / Slow).</param>
/// <param name="Primary">The headline value, e.g. "12 sat/vB", "1.4 Gwei", "0.00012 XMR".</param>
/// <param name="Secondary">Supporting line — ETA and/or estimated cost for a typical tx.</param>
/// <param name="Usd">Approx. cost of a typical transaction in USD, e.g. "$0.08" (null if unknown).</param>
public sealed record NetworkFeeTier(string Label, string Primary, string Secondary, string? Usd = null);

/// <summary>
/// The representative ("Normal") data point logged to the time-series for charting:
/// a native rate plus the typical-tx cost in USD.
/// </summary>
public sealed record NetworkFeeSample(decimal Native, string NativeUnit, decimal? UsdPerTx);

/// <summary>
/// A snapshot of the current on-chain fee for one network, normalized so the
/// three coins (Bitcoin / Ethereum / Monero) render through the same UI block.
/// Produced by <c>NetworkFeeService</c> and cached by the warmer.
/// </summary>
public sealed record NetworkFee(
    string Network,                       // "bitcoin" | "ethereum" | "monero"
    string Title,                         // "Bitcoin Network Fee" etc.
    IReadOnlyList<NetworkFeeTier> Tiers,
    string? Note,
    DateTimeOffset UpdatedAtUtc,
    NetworkFeeSample? Sample = null)
{
    public bool HasData => Tiers is { Count: > 0 };
}
