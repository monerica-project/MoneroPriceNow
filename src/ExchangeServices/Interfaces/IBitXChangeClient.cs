using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Implementations;

public interface IBitXChangeClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{
}
