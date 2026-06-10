namespace CryptoPriceNow.Data.Entities;

/// <summary>
/// One captured quote for one exchange + trading pair at a point in time.
/// Buy  = quote units paid per 1 base (USDT you spend to receive 1 XMR).
/// Sell = quote units received per 1 base (USDT you get for sending 1 XMR).
/// Either may be null if the exchange only supports one direction.
/// </summary>
public sealed class PriceQuote
{
    public long Id { get; set; }

    public int ExchangeId { get; set; }
    public Exchange? Exchange { get; set; }

    /// <summary>Normalized trading pair, e.g. "XMR/USDT:Tron".</summary>
    public string Pair { get; set; } = string.Empty;

    public decimal? Buy { get; set; }
    public decimal? Sell { get; set; }

    /// <summary>"float" or "fixed" at the time the quote was captured.</summary>
    public string RateType { get; set; } = "float";

    /// <summary>UTC timestamp of the quote (the exchange fetch time). Indexed for charting.</summary>
    public DateTimeOffset TimestampUtc { get; set; }
}
