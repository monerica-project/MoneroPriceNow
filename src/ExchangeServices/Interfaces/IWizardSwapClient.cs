using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IWizardSwapClient : IExchangeCurrencyApi, IExchangePriceApi, IExchangeBuyPriceApi
{
}