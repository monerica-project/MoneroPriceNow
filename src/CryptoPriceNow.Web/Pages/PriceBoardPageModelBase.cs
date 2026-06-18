using System.Text.Json;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>
/// Shared loading logic for every pair page. A subclass sets <see cref="Pair"/>
/// (fixed for the root page, route-derived for /xmr-{quote}); this base fetches
/// the warmer's snapshot for that pair and serializes it for instant first paint.
/// </summary>
public abstract class PriceBoardPageModelBase : PageModel
{
    private readonly IPriceService _prices;

    protected PriceBoardPageModelBase(IPriceService prices) => _prices = prices;

    /// <summary>The pair this page renders. Set by the subclass before LoadAsync.</summary>
    public PriceBoardView Pair { get; protected set; } = PairCatalog.Usdt;

    public async Task LoadAsync(CancellationToken ct)
    {
        ViewData["Title"] = Pair.Title;

        try
        {
            // PriceService is a singleton backed by PriceWarmingService.
            // After warm-up this returns from memory in microseconds.
            var rows = await _prices.GetTwoWayPricesAsync(Pair.Base, Pair.ApiQuote, ct);

            ViewData["InitialPricesJson"] = JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            // Fall back to empty — JS will fetch via AJAX on boot as before.
            ViewData["InitialPricesJson"] = "[]";
        }
    }
}
