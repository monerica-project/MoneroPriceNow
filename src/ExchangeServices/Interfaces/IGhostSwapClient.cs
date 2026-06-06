using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IGhostSwapClient
    : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi,
      IPrivacyLevel, IMinAmountUsd
{
}
