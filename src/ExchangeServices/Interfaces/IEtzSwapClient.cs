using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IEtzSwapClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel, IMinAmountUsd
{
}