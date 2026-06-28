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
    private readonly INetworkFeeService _fees;

    protected PriceBoardPageModelBase(IPriceService prices, INetworkFeeService fees)
    {
        _prices = prices;
        _fees = fees;
    }

    /// <summary>The pair this page renders. Set by the subclass before LoadAsync.</summary>
    public PriceBoardView Pair { get; protected set; } = PairCatalog.Usdt;

    /// <summary>Current on-chain fee for this page's network (null if unavailable).</summary>
    public NetworkFee? NetworkFee { get; private set; }

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

        // On-chain fee for this page's network (Monero / Bitcoin / Ethereum).
        if (!string.IsNullOrEmpty(Pair.FeeNetwork))
        {
            try { NetworkFee = await _fees.GetFeeAsync(Pair.FeeNetwork, ct); }
            catch { NetworkFee = null; }
        }

        // Shared with the _PriceBoard partial (whose model is the PriceBoardView).
        ViewData["NetworkFee"] = NetworkFee;
    }
}
