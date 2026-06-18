namespace CryptoPriceNow.Web.Models;

/// <summary>
/// Everything a price page (and its JS) needs to render one trading pair.
/// One instance per supported quote currency. This is the single source of
/// truth for the warmer, the /api/history allow-list, the page routes, and the
/// per-page JS config object (window.__PAIR__).
///
/// To add a new pair later: add one entry to <see cref="PairCatalog.All"/>.
/// Exchanges that can't quote the pair simply return null and drop off the
/// table automatically — no per-pair wiring required here.
/// </summary>
public sealed record PriceBoardView
{
    /// <summary>Base asset ticker. Always XMR for this site.</summary>
    public required string Base { get; init; }

    /// <summary>
    /// The quote string sent to /api/prices and /api/prices/two-way.
    /// This is what PriceService.ParseAsset() understands
    /// (e.g. "USDTTRC", "BTC", "ETH").
    /// </summary>
    public required string ApiQuote { get; init; }

    /// <summary>
    /// Normalized DB pair label produced by PriceService.BuildPairLabel and
    /// stored on every PriceQuote row. Used by /api/history.
    /// (e.g. "XMR/USDT:Tron", "XMR/BTC", "XMR/ETH").
    /// </summary>
    public required string HistoryPair { get; init; }

    /// <summary>Short quote label shown in the UI (e.g. "USDT", "BTC", "ETH").</summary>
    public required string QuoteLabel { get; init; }

    /// <summary>e.g. "$ per XMR", "BTC per XMR", "ETH per XMR" — used in column headers.</summary>
    public required string PriceUnitLabel { get; init; }

    /// <summary>Currency symbol prefix for formatted prices ("$" for USD pairs, "" otherwise).</summary>
    public required string Symbol { get; init; }

    /// <summary>Currency suffix for formatted prices ("" for USD, " BTC", " ETH").</summary>
    public required string Suffix { get; init; }

    /// <summary>Decimal places to show for a price in this quote.</summary>
    public required int Decimals { get; init; }

    /// <summary>
    /// Smallest value treated as a real (non-garbage) price. The front-end
    /// rejects anything below this. USD pairs use 1 (XMR is never &lt; $1);
    /// crypto pairs use a tiny floor so legitimate sub-1 ratios are kept.
    /// </summary>
    public required decimal MinValidPrice { get; init; }

    /// <summary>True when the quote is a USD-pegged stablecoin (enables the $ converter semantics).</summary>
    public required bool IsUsd { get; init; }

    /// <summary>&lt;title&gt; text for the page.</summary>
    public required string Title { get; init; }

    /// <summary>Route slug. Empty string = site root "/". Otherwise e.g. "xmr-btc".</summary>
    public required string Slug { get; init; }

    /// <summary>Relative URL for this page ("/" for root).</summary>
    public string Url => string.IsNullOrEmpty(Slug) ? "/" : "/" + Slug;
}

/// <summary>
/// The supported trading pairs. Add an entry here to surface a new pair across
/// the warmer, the history API, the page routes, and the tab strip.
/// </summary>
public static class PairCatalog
{
    public static readonly PriceBoardView Usdt = new()
    {
        Base = "XMR",
        ApiQuote = "USDTTRC",
        HistoryPair = "XMR/USDT:Tron",
        QuoteLabel = "USDT",
        PriceUnitLabel = "$ per XMR",
        Symbol = "$",
        Suffix = "",
        Decimals = 2,
        MinValidPrice = 1m,
        IsUsd = true,
        Title = "MoneroPriceNow.com — Live XMR / USDT Prices",
        Slug = "" // root
    };

    public static readonly PriceBoardView Btc = new()
    {
        Base = "XMR",
        ApiQuote = "BTC",
        HistoryPair = "XMR/BTC",
        QuoteLabel = "BTC",
        PriceUnitLabel = "BTC per XMR",
        Symbol = "",
        Suffix = " BTC",
        Decimals = 6,
        MinValidPrice = 0.0000001m,
        IsUsd = false,
        Title = "MoneroPriceNow.com — Live XMR / BTC Prices",
        Slug = "xmr-btc"
    };

    public static readonly PriceBoardView Eth = new()
    {
        Base = "XMR",
        ApiQuote = "ETH",
        HistoryPair = "XMR/ETH",
        QuoteLabel = "ETH",
        PriceUnitLabel = "ETH per XMR",
        Symbol = "",
        Suffix = " ETH",
        Decimals = 5,
        MinValidPrice = 0.000001m,
        IsUsd = false,
        Title = "MoneroPriceNow.com — Live XMR / ETH Prices",
        Slug = "xmr-eth"
    };

    /// <summary>All supported pairs, in tab-display order.</summary>
    public static readonly IReadOnlyList<PriceBoardView> All = [Usdt, Btc, Eth];

    /// <summary>Resolve a non-root pair by its route slug (e.g. "xmr-btc"). Null if unknown.</summary>
    public static PriceBoardView? BySlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return Usdt;
        slug = slug.Trim().ToLowerInvariant();
        return All.FirstOrDefault(p =>
            !string.IsNullOrEmpty(p.Slug) &&
            p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    }
}
