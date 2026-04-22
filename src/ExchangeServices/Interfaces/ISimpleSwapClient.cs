using ExchangeServices.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace ExchangeServices.Interfaces
{
    public interface ISimpleSwapClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
    { }
}
