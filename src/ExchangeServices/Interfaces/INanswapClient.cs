using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface INanswapClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi, IPrivacyLevel, IMinAmountUsd
{
    // No extra members needed; we implement Buy via IExchangePriceApi
    // (assuming your IExchangePriceApi already has GetBuyPriceAsync like your other clients).
}