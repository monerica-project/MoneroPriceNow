using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Implementations;

public interface IChangeHeroClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{
}
