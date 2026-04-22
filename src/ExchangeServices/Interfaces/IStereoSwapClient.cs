using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Implementations;

public interface IStereoSwapClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{
}
