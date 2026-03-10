using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IChangeeClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel
{
}
