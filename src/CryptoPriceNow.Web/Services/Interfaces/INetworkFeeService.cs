using CryptoPriceNow.Web.Models;

namespace CryptoPriceNow.Services;

/// <summary>
/// Provides the current on-chain network fee for a coin's network. Backed by a
/// process-wide cache that <c>NetworkFeeWarmingService</c> keeps fresh.
/// </summary>
public interface INetworkFeeService
{
    /// <summary>The networks this service can report ("bitcoin", "ethereum", "monero").</summary>
    IReadOnlyList<string> Networks { get; }

    /// <summary>
    /// Returns the cached fee for a network, fetching once on a cold cache.
    /// Returns null if the network is unknown or the upstream is unavailable.
    /// </summary>
    Task<NetworkFee?> GetFeeAsync(string network, CancellationToken ct = default);

    /// <summary>Force a live refresh of one network's fee into the cache.</summary>
    Task RefreshAsync(string network, CancellationToken ct = default);
}
