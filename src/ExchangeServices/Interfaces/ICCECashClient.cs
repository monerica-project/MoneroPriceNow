using ExchangeServices.Models;
using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface ICCECashClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel
{
    string ExchangeKey { get; }

    Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default);       // sell
    Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default);    // buy
    Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default);
}