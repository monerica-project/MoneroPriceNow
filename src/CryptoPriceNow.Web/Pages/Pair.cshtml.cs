using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace CryptoPriceNow.Pages;

public sealed class PairModel : PriceBoardPageModelBase
{
    public PairModel(IPriceService prices) : base(prices) { }

    // Route is /xmr-{quote}; the full slug is "xmr-{quote}".
    public async Task<IActionResult> OnGetAsync(string quote, CancellationToken ct)
    {
        var slug = $"xmr-{(quote ?? string.Empty).Trim().ToLowerInvariant()}";
        var resolved = PairCatalog.BySlug(slug);

        if (resolved is null)
            return NotFound();

        // The USDT pair lives at the root "/" — keep it canonical there.
        if (ReferenceEquals(resolved, PairCatalog.Usdt))
            return RedirectPermanent("/");

        Pair = resolved;
        await LoadAsync(ct);
        return Page();
    }
}
