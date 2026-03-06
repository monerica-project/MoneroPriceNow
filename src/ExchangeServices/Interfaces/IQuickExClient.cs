using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

/// <summary>
/// Quickex.io swap exchange client.
/// Supports sell prices (XMR → USDT), buy prices (USDT → XMR), and currency listing.
/// Uses the public v1 API (no auth) by default; upgrades to HMAC-signed v2 when
/// ApiPublicKey + ApiPrivateKey are configured.
/// </summary>
public interface IQuickExClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi
{
}
