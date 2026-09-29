using CryptoPriceNow.Web.Models;
using ExchangeServices.Abstractions;

namespace CryptoPriceNow.Services;

public interface IPriceService
{
    Task<IReadOnlyList<PriceResult>> GetPricesAsync(string @base, string quote, CancellationToken ct = default);
    Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(string exchangeKey, CancellationToken ct = default);

    Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesAsync(string @base, string quote, CancellationToken ct = default);

    /// <summary>
    /// One-way SELL rows for base → quote (i.e. selling XMR for the quote asset), carrying the
    /// same probe-sized rate + metadata as the board. Unlike <see cref="GetTwoWayPricesAsync"/>
    /// this does NOT require a buy side, so sell-only venues (e.g. xmr2cex) are included — used
    /// by the /swap page for the XMR → asset direction. Rows whose Sell is absent are dropped.
    /// </summary>
    Task<IReadOnlyList<TwoWayPriceRow>> GetSellRowsAsync(string @base, string quote, CancellationToken ct = default);
}