using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Implementations;

public interface ISwapgateClient
    : IExchangePriceApi,
      IExchangeBuyPriceApi,
      IExchangeCurrencyApi,
      IPrivacyLevel
{
}
