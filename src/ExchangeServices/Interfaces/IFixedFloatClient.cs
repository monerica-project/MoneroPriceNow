using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IFixedFloatClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel, IMinAmountUsd
{
}