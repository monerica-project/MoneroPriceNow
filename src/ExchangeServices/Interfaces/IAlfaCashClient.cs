using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Interfaces;

public interface IAlfaCashClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{
}
