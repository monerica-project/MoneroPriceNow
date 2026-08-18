using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace CryptoPriceNow.Pages;

// Fixed-rate view of a pair. /fixed = the root XMR/USDT pair; /fixed/xmr-btc, /fixed/xmr-eth, …
// mirror the float pages ("/", "/xmr-btc", …). Same board, RateType = "fixed".
public sealed class FixedModel : PriceBoardPageModelBase
{
    public FixedModel(IPriceService prices, INetworkFeeService fees) : base(prices, fees) { }

    public async Task<IActionResult> OnGetAsync(string? slug, CancellationToken ct)
    {
        var resolved = string.IsNullOrWhiteSpace(slug)
            ? PairCatalog.Usdt
            : PairCatalog.BySlug(slug.Trim().ToLowerInvariant());

        if (resolved is null)
        {
            return NotFound();
        }

        // The root pair's fixed page is canonical at /fixed — redirect any explicit USDT slug there.
        if (!string.IsNullOrWhiteSpace(slug) && ReferenceEquals(resolved, PairCatalog.Usdt))
        {
            return RedirectPermanent("/fixed");
        }

        Pair = resolved;
        RateType = "fixed";
        await LoadAsync(ct);

        // Distinct title/description so the fixed page isn't a duplicate of the float page.
        ViewData["Title"] = $"Fixed-Rate XMR → {Pair.QuoteLabel} Swap Prices | MoneroPriceNow";
        ViewData["Description"] =
            $"Live fixed-rate exchange prices for swapping Monero (XMR) to {Pair.QuoteLabel} — the rate is " +
            "locked in when the swap starts. Compare privacy-friendly instant exchanges side by side.";

        return Page();
    }
}
