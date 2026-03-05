using ExchangeServices.Abstractions;

namespace ExchangeServices.SageSwap;

public interface ISageSwapClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi
{
}