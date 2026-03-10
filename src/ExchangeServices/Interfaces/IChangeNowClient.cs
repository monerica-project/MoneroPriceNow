using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IChangeNowClient : IExchangeCurrencyApi, IExchangePriceApi, IExchangeBuyPriceApi, IPrivacyLevel
{
}