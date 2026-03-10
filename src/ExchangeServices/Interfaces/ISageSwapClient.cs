using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.SageSwap;

public interface ISageSwapClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel
{
}