using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IDevilExchangeClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel
{
}