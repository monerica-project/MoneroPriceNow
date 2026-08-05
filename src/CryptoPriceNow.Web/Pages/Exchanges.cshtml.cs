using CryptoPriceNow.Data.Entities;
using CryptoPriceNow.Data.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>
/// Browsable index of every tracked exchange at /exchanges. Its plain &lt;a&gt; links to
/// each /exchange/{slug} page are the crawl path into those pages (the homepage rows are
/// rendered in JS, so search engines can't follow them).
/// </summary>
public sealed class ExchangesModel : PageModel
{
    private readonly ExchangeDirectoryService _directory;

    public ExchangesModel(ExchangeDirectoryService directory) => _directory = directory;

    public IReadOnlyList<Exchange> Exchanges { get; private set; } = System.Array.Empty<Exchange>();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Exchanges = await _directory.GetActiveAsync(ct);

        ViewData["Title"] = "Monero Exchanges — Live XMR Prices by Exchange";
        ViewData["Description"] =
            "Every exchange we track live Monero (XMR) prices for. Open any to see its real " +
            "XMR/USD, XMR/BTC and XMR/ETH buy and sell prices, chart, and history.";
    }

    public static string Slug(string siteName) => ExchangeSlug.From(siteName);
}
