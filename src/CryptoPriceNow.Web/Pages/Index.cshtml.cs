using System.Text.Json;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IPriceService _prices;

    // Serialized price data injected into the page — JS reads this on boot
    // and renders immediately without waiting for an AJAX round-trip.
    public string InitialPricesJson { get; private set; } = "[]";

    public IndexModel(IPriceService prices)
    {
        _prices = prices;
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            // PriceService is a singleton backed by PriceWarmingService.
            // After warm-up this returns from memory in microseconds.
            var rows = await _prices.GetTwoWayPricesAsync("XMR", "USDTTRC", ct);

            InitialPricesJson = JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            // Fall back to empty — JS will fetch via AJAX on boot as before.
            InitialPricesJson = "[]";
        }
    }
}
