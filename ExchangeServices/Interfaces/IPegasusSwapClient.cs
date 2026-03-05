using ExchangeServices.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace ExchangeServices.Interfaces
{ 
    public interface IPegasusSwapClient : IExchangePriceApi, IExchangeCurrencyApi, IExchangeBuyPriceApi
    {
    }
}
