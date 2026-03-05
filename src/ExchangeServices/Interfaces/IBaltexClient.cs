using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IBaltexClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi
{
}