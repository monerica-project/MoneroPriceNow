using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IZeroTraceClient
    : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi,
      IPrivacyLevel, IMinAmountUsd
{
}
