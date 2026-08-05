using CryptoPriceNow.Data.Entities;
using CryptoPriceNow.Data.Services;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>
/// Per-exchange page at /exchange/{name}. Shows this one exchange's current buy/sell for
/// each pair it quotes (XMR/USDT, XMR/BTC, XMR/ETH), split by rate type (floating vs fixed
/// when the exchange offers both), a per-exchange chart, and a link to its Monerica profile.
/// The homepage exchange name links here.
/// </summary>
public sealed class ExchangeModel : PageModel
{
    private readonly IPriceService _prices;
    private readonly ExchangeDirectoryService _directory;

    public ExchangeModel(IPriceService prices, ExchangeDirectoryService directory)
    {
        _prices = prices;
        _directory = directory;
    }

    public Exchange Exchange { get; private set; } = default!;
    public string Slug { get; private set; } = string.Empty;

    public string MonericaUrl => $"https://monerica.com/site/{Slug}";

    /// <summary>Current prices for each pair this exchange quotes, in tab order.</summary>
    public List<PairPrice> Pairs { get; } = new();

    /// <summary>True when at least one pair has a floating-rate quote.</summary>
    public bool HasFloat { get; private set; }

    /// <summary>True when at least one pair has a fixed-rate quote.</summary>
    public bool HasFixed { get; private set; }

    /// <summary>Rate type shown first: "float" if available, else "fixed".</summary>
    public string DefaultRate => HasFloat ? "float" : "fixed";

    /// <summary>Both rate types present → the page shows a Float/Fixed selector.</summary>
    public bool HasBothRates => HasFloat && HasFixed;

    public sealed record Side(decimal? Buy, decimal? Sell);

    public sealed record PairPrice(PriceBoardView Pair, Side? Float, Side? Fixed)
    {
        public string Format(decimal? v)
        {
            if (v is null)
            {
                return "—";
            }

            var n = v.Value.ToString("N" + Pair.Decimals, System.Globalization.CultureInfo.InvariantCulture);
            return $"{Pair.Symbol}{n}{Pair.Suffix}";
        }
    }

    public async Task<IActionResult> OnGetAsync(string name, CancellationToken ct)
    {
        Slug = (name ?? string.Empty).Trim().ToLowerInvariant();

        var exchange = await _directory.GetBySlugAsync(Slug, ct);
        if (exchange is null)
        {
            return NotFound();
        }

        Exchange = exchange;

        foreach (var pair in PairCatalog.All)
        {
            IReadOnlyList<TwoWayPriceRow> rows;
            try { rows = await _prices.GetTwoWayPricesAsync(pair.Base, pair.ApiQuote, ct); }
            catch { continue; }

            // The warmer returns one row per (exchange, rate type), so a both-rate exchange
            // appears twice — keep them separate rather than averaging them together.
            var mine = rows
                .Where(r => string.Equals(r.Exchange, exchange.ExchangeKey, StringComparison.OrdinalIgnoreCase)
                            && (r.Buy is not null || r.Sell is not null))
                .ToList();

            if (mine.Count == 0)
            {
                continue;
            }

            Side? floatSide = ToSide(mine.FirstOrDefault(r => IsFloat(r.RateType)));
            Side? fixedSide = ToSide(mine.FirstOrDefault(r => !IsFloat(r.RateType)));

            if (floatSide is not null) HasFloat = true;
            if (fixedSide is not null) HasFixed = true;

            Pairs.Add(new PairPrice(pair, floatSide, fixedSide));
        }

        ViewData["Title"] = $"{exchange.SiteName} Monero (XMR) Price — Live Rates & Chart";
        ViewData["Description"] =
            $"Live Monero (XMR) buy and sell prices on {exchange.SiteName}: real XMR/USD, XMR/BTC and XMR/ETH rates, " +
            $"an interactive chart, and price history.";

        return Page();
    }

    private static bool IsFloat(string? rateType)
        => !string.Equals(rateType, "fixed", StringComparison.OrdinalIgnoreCase);

    private static Side? ToSide(TwoWayPriceRow? r)
        => r is null ? null : new Side(r.Buy, r.Sell);
}
