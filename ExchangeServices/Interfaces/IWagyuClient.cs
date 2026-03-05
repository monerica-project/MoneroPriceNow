using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;

namespace ExchangeServices.Implementations;

public interface IWagyuClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi
{
}