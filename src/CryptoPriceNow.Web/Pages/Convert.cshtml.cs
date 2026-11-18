using CryptoPriceNow.Web.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>/convert/xmr-to-{currency}: what an amount of Monero is worth in a given currency, both ways.</summary>
public sealed class ConvertModel : PageModel
{
    public static readonly decimal[] XmrAmounts = [0.1m, 0.5m, 1m, 2m, 5m, 10m, 25m, 50m, 100m];
    public static readonly decimal[] FiatAmounts = [10m, 50m, 100m, 500m, 1000m, 5000m];

    private readonly MillionaireService _price;
    private readonly FxRateService _fx;

    public ConvertModel(MillionaireService price, FxRateService fx)
    {
        _price = price;
        _fx = fx;
    }

    public Fiat Fiat { get; private set; } = FiatCatalog.All[0];
    public decimal Amount { get; private set; } = 1m;

    /// <summary>Price of one XMR in the page's currency; null while prices or rates are unavailable.</summary>
    public decimal? PricePerXmr { get; private set; }
    public decimal? UsdPrice { get; private set; }
    public DateOnly? FxDate { get; private set; }

    public async Task<IActionResult> OnGetAsync(string fiat, decimal? amount, CancellationToken ct)
    {
        var found = FiatCatalog.Find(fiat);
        if (found is null)
        {
            return NotFound();
        }

        Fiat = found;
        if (amount is > 0m and <= 1_000_000_000m)
        {
            Amount = amount.Value;
        }

        UsdPrice = (await _price.GetAsync(ct)).Price;
        if (UsdPrice is decimal usd)
        {
            if (found.Code == "USD")
            {
                PricePerXmr = usd;
            }
            else if (await _fx.GetAsync(ct) is { } rates && rates.UsdTo.TryGetValue(found.Code, out var rate))
            {
                PricePerXmr = usd * rate;
                FxDate = rates.Date;
            }
        }

        return Page();
    }
}
