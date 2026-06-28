using CryptoPriceNow.Data.Models;

namespace CryptoPriceNow.Data.Interfaces;

/// <summary>
/// Fire-and-forget intake for network-fee samples. NetworkFeeService calls this
/// after each warm cycle; the implementation queues the sample and returns
/// immediately so fee serving is never blocked by the database.
/// </summary>
public interface INetworkFeeQuoteSink
{
    ValueTask EnqueueAsync(NetworkFeeSnapshot snapshot, CancellationToken ct = default);
}

/// <summary>Registered when no ConnectionStrings:PriceDb is configured — fees just aren't logged.</summary>
public sealed class NullNetworkFeeQuoteSink : INetworkFeeQuoteSink
{
    public ValueTask EnqueueAsync(NetworkFeeSnapshot snapshot, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
