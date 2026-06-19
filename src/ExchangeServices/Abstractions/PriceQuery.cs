namespace ExchangeServices.Abstractions;

public sealed record PriceQuery(AssetRef Base, AssetRef Quote, decimal? ProbeAmount = null)
{
    public string Key => $"{Base.Key}->{Quote.Key}";
}

// ProbeAmount: optional buy-side probe size, denominated in the Quote currency.
// Clients that quote the buy direction by sending a fixed amount and measuring
// how much XMR comes back should use `query.ProbeAmount ?? <their own default>`.
// PriceService sets this per quote (BTC/ETH) so the probe clears each exchange's
// minimum without blowing past its maximum. Null = use the client's own default
// (keeps the existing USDT behaviour untouched).
