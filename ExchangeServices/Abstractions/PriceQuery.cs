namespace ExchangeServices.Abstractions;

public sealed record PriceQuery(AssetRef Base, AssetRef Quote)
{
    public string Key => $"{Base.Key}->{Quote.Key}";
}