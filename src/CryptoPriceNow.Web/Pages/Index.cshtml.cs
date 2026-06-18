using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;

namespace CryptoPriceNow.Pages;

public sealed class IndexModel : PriceBoardPageModelBase
{
    public IndexModel(IPriceService prices) : base(prices) { }

    public Task OnGetAsync(CancellationToken ct)
    {
        // Root "/" stays XMR/USDT — preserves the existing ranked URL.
        Pair = PairCatalog.Usdt;
        return LoadAsync(ct);
    }
}
