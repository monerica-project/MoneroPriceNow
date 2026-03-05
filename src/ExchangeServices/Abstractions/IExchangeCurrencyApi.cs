namespace ExchangeServices.Abstractions;

public sealed record ExchangeCurrency(string ExchangeId, string Ticker, string Network);

public interface IExchangeCurrencyApi
{
    string ExchangeKey { get; }
    Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default);
}