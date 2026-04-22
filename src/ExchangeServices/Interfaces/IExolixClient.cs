using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IExolixClient : IExchangeCurrencyApi, IExchangePriceApi, IExchangeBuyPriceApi, IPrivacyLevel, IMinAmountUsd
{
    // marker interface (same pattern as your other clients)
}