using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IFuguSwapClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel
{
}