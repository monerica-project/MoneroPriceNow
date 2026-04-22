using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IOctoSwapClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel, IMinAmountUsd
{
}