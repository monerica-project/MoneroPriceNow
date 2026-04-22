using ExchangeServices.Abstractions;
using ExchangeServices.Models;

namespace ExchangeServices.Interfaces;

public interface IGoDexClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{
}
