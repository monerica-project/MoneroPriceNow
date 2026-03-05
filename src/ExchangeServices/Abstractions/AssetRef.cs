namespace ExchangeServices.Abstractions;

public sealed record AssetRef(string Ticker, string? Network = null, string? ExchangeId = null)
{
    public string Key => $"{Ticker}:{Network ?? ""}:{ExchangeId ?? ""}".ToLowerInvariant();
}