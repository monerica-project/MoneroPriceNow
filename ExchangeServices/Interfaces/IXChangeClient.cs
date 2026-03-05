using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IXChangeClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi
{
}