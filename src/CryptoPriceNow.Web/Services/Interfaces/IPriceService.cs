using CryptoPriceNow.Web.Models;
using ExchangeServices.Abstractions;

namespace CryptoPriceNow.Services;

public interface IPriceService
{
    Task<IReadOnlyList<PriceResult>> GetPricesAsync(string @base, string quote, CancellationToken ct = default);
    Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(string exchangeKey, CancellationToken ct = default);

    Task<IReadOnlyList<TwoWayPriceRow>> GetTwoWayPricesAsync(string @base, string quote, CancellationToken ct = default);
}