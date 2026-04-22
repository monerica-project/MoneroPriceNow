namespace ExchangeServices.Abstractions;

public sealed record PriceResult(
    string Exchange,
    AssetRef Base,
    AssetRef Quote,
    decimal Price,              // 1 Base = Price Quote
    DateTimeOffset TimestampUtc,
    string? CorrelationId = null,
    string? Raw = null,
    decimal? MinAmountUsd = null
);