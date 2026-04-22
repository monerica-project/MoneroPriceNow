using ExchangeServices.Abstractions;
using ExchangeServices.Models;

namespace ExchangeServices.Interfaces;

public interface IXgramClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel, IMinAmountUsd
{
    string ExchangeKey { get; }

    Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default);      // SELL
    Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default);   // BUY
    Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default);
}