using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Interfaces;

public interface ITrocadorClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{
}