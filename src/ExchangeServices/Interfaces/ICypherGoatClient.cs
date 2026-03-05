using ExchangeServices.Abstractions;
using ExchangeServices.Models;

namespace ExchangeServices.Interfaces;

public interface ICypherGoatClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi
{
    string ExchangeKey { get; }

    Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default);     // SELL
    Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default); // BUY
}