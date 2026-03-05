using ExchangeServices.Abstractions;
using ExchangeServices.Models;

namespace ExchangeServices.Interfaces;

public interface ISwapuzClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi
{
}
