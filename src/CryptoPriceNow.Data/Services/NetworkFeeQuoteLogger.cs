using System.Threading.Channels;
using CryptoPriceNow.Data.Entities;
using CryptoPriceNow.Data.Interfaces;
using CryptoPriceNow.Data.Models;
using CryptoPriceNow.Data.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPriceNow.Data.Services;

/// <summary>
/// Single-consumer background writer for network-fee samples. Mirrors
/// PriceQuoteLogger: NetworkFeeService enqueues (non-blocking); this drains the
/// channel, throttles per network, inserts NetworkFeeQuote rows, and prunes old
/// rows per RetentionDays. All failures are logged and swallowed.
/// </summary>
public sealed class NetworkFeeQuoteLogger : BackgroundService, INetworkFeeQuoteSink
{
    private readonly IDbContextFactory<PriceDbContext> _dbFactory;
    private readonly ILogger<NetworkFeeQuoteLogger> _log;
    private readonly FeeLoggingOptions _opt;

    private readonly Channel<NetworkFeeSnapshot> _channel =
        Channel.CreateBounded<NetworkFeeSnapshot>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    // network -> last sample capture time logged (throttle)
    private readonly Dictionary<string, DateTimeOffset> _lastLogged = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRetentionRun = DateTimeOffset.MinValue;

    public NetworkFeeQuoteLogger(
        IDbContextFactory<PriceDbContext> dbFactory,
        IOptions<FeeLoggingOptions> options,
        ILogger<NetworkFeeQuoteLogger> log)
    {
        _dbFactory = dbFactory;
        _log = log;
        _opt = options.Value;
    }

    public ValueTask EnqueueAsync(NetworkFeeSnapshot snapshot, CancellationToken ct = default)
    {
        if (_opt.Enabled)
            _channel.Writer.TryWrite(snapshot);
        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("[FeeLogger] Disabled via FeeLogging:Enabled=false");
            return;
        }

        _log.LogInformation("[FeeLogger] Started — interval={Interval}s retention={Retention}d",
            _opt.LogIntervalSeconds, _opt.RetentionDays);

        try
        {
            await foreach (var snapshot in _channel.Reader.ReadAllAsync(ct))
            {
                try { await ProcessAsync(snapshot, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[FeeLogger] Failed to log sample for {Network}", snapshot.Network);
                }

                try { await RunRetentionIfDueAsync(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.LogWarning(ex, "[FeeLogger] Retention pruning failed"); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task ProcessAsync(NetworkFeeSnapshot s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Network)) return;

        var minGap = TimeSpan.FromSeconds(Math.Clamp(_opt.LogIntervalSeconds, 5, 3600));
        if (_lastLogged.TryGetValue(s.Network, out var last) && s.CapturedUtc - last < minGap)
            return;
        _lastLogged[s.Network] = s.CapturedUtc;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.NetworkFeeQuotes.Add(new NetworkFeeQuote
        {
            Network = s.Network.ToLowerInvariant(),
            Native = s.Native,
            NativeUnit = s.NativeUnit,
            UsdPerTx = s.UsdPerTx,
            TimestampUtc = s.CapturedUtc.ToUniversalTime()
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task RunRetentionIfDueAsync(CancellationToken ct)
    {
        if (_opt.RetentionDays <= 0) return;

        var now = DateTimeOffset.UtcNow;
        if (now - _lastRetentionRun < TimeSpan.FromHours(6)) return;
        _lastRetentionRun = now;

        var cutoff = now.AddDays(-_opt.RetentionDays);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.NetworkFeeQuotes
            .Where(q => q.TimestampUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
            _log.LogInformation("[FeeLogger] Retention: deleted {Count} fee samples older than {Cutoff:u}",
                deleted, cutoff);
    }
}
