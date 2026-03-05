using System.Threading;
using System.Threading.Tasks;

namespace ExchangeServices.Abstractions;

/// <summary>
/// Optional capability: quote needed to BUY 1 base using quote asset.
/// Returns PriceResult where Price = quote-per-1-base.
/// Example: base=XMR, quote=USDTTRC -> Price = USDT needed to receive 1 XMR.
/// </summary>
public interface IExchangeBuyPriceApi
{
    Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default);
}