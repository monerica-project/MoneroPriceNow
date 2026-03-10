using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IBitcoinVNClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel
{
}
