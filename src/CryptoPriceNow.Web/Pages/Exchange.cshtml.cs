using CryptoPriceNow.Data.Entities;
using CryptoPriceNow.Data.Services;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
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
    private readonly IEnumerable<IExchangePriceApi> _apis;

    public ExchangeModel(IPriceService prices, ExchangeDirectoryService directory, IEnumerable<IExchangePriceApi> apis)
    {
        _prices = prices;
        _directory = directory;
        _apis = apis;
    }

    public Exchange Exchange { get; private set; } = default!;
    public string Slug { get; private set; } = string.Empty;

    /// <summary>True for a one-way venue (e.g. sell-only xmr2cex) that has no two-way board data:
    /// we render a light profile page linking to its Monerica listing instead of charts.</summary>
    public bool IsFiller { get; private set; }

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
            // Not on the two-way price board (e.g. a one-way, sell-only venue like xmr2cex that
            // never posts board data). If it's still a registered exchange client, show a light
            // filler profile that links to its Monerica listing rather than returning a 404.
            var api = _apis.FirstOrDefault(a =>
                string.Equals(ExchangeSlug.From(a.SiteName), Slug, StringComparison.Ordinal));
            if (api is null)
            {
                return NotFound();
            }

            Exchange = new Exchange
            {
                ExchangeKey = api.ExchangeKey,
                SiteName = api.SiteName,
                SiteUrl = api.SiteUrl,
                PrivacyLevel = (api as IPrivacyLevel)?.PrivacyLevel.ToString(),
                IsActive = true,
            };
            IsFiller = true;
            ViewData["Title"] = $"{api.SiteName} — Monero (XMR) Exchange | MoneroPriceNow";
            ViewData["Description"] =
                $"{api.SiteName} is a Monero exchange in the MoneroPriceNow directory. See its full profile on Monerica.";
            return Page();
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
