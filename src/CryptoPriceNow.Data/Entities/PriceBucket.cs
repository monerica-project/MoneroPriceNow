namespace CryptoPriceNow.Data.Entities;

/// <summary>
/// Pre-aggregated 1-minute rollup of <see cref="PriceQuote"/>, keyed by
/// (Pair, RateType, Bucket). The chart reads these instead of the raw quote
/// stream, so a 30-day view touches a few thousand rows instead of millions.
///
/// Sums and counts are stored (not averages) so any coarser output bucket
/// re-aggregates exactly: avg = SUM(Sum*)/SUM(*Count). Buy and Sell are counted
/// separately because either can be null on a given quote.
/// </summary>
public sealed class PriceBucket
{
    /// <summary>Normalized trading pair, e.g. "XMR/USDT:Tron".</summary>
    public string Pair { get; set; } = string.Empty;

    /// <summary>"float" or "fixed".</summary>
    public string RateType { get; set; } = "float";

    /// <summary>Start of the 1-minute bucket (UTC), aligned to the date_bin origin.</summary>
    public DateTimeOffset Bucket { get; set; }

    public decimal SumBuy { get; set; }
    public int BuyCount { get; set; }
    public decimal SumSell { get; set; }
    public int SellCount { get; set; }

    /// <summary>Total raw quotes folded into this bucket.</summary>
    public int Samples { get; set; }
}
