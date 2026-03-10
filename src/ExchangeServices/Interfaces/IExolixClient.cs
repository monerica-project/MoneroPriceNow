using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IExolixClient : IExchangeCurrencyApi, IExchangePriceApi, IExchangeBuyPriceApi, IPrivacyLevel
{
    // marker interface (same pattern as your other clients)
}