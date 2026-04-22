using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface ILetsExchangeClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{
}