using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

public class IndexModel : PageModel
{
    private readonly CryptoPriceNow.Services.IPriceService prices;

    public IndexModel(CryptoPriceNow.Services.IPriceService prices) => this.prices = prices;

    public IReadOnlyList<decimal> XmrPrices { get; private set; } = Array.Empty<decimal>();

    public async Task OnGetAsync()
    {
        var results = await prices.GetPricesAsync("XMR", "USDTTRC", HttpContext.RequestAborted);
        XmrPrices = results.Select(r => r.Price).ToList();
    }
}