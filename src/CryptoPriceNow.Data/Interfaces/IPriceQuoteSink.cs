using CryptoPriceNow.Data.Models;

namespace CryptoPriceNow.Data.Interfaces;

/// <summary>
/// Fire-and-forget intake for price snapshots. PriceService calls this after
/// every warm cycle; the implementation queues the snapshot and returns
/// immediately so price serving is never blocked by the database.
/// </summary>
public interface IPriceQuoteSink
{
    ValueTask EnqueueAsync(QuoteSnapshot snapshot, CancellationToken ct = default);
}

/// <summary>
/// Registered when no ConnectionStrings:PriceDb is configured — the site runs
/// exactly as before, with zero database dependency.
/// </summary>
public sealed class NullPriceQuoteSink : IPriceQuoteSink
{
    public ValueTask EnqueueAsync(QuoteSnapshot snapshot, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
