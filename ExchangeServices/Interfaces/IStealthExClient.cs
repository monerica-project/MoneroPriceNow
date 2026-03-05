using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IStealthExClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi
{
}