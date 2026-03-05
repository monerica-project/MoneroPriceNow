namespace ExchangeServices.Abstractions;

/// <summary>
/// Core price API every exchange client must implement.
/// SiteName / SiteUrl are used for display and affiliate linking in the UI.
/// </summary>
public interface IExchangePriceApi
{
    /// <summary>Internal key used for caching and logging (e.g. "fixedfloat").</summary>
    string ExchangeKey { get; }

    /// <summary>Human-readable display name shown in the UI (e.g. "FixedFloat").</summary>
    string SiteName { get; }

    /// <summary>Affiliate or referral URL opened when user clicks the exchange name. Null if none.</summary>
    string? SiteUrl { get; }

    Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default);
}
