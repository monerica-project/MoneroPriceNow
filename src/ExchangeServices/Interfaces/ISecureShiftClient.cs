using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Implementations;

public interface ISecureShiftClient
    : IExchangePriceApi,
      IExchangeBuyPriceApi,
      IExchangeCurrencyApi,
      IPrivacyLevel
{
}
