using CryptoPriceNow.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPriceNow.Web.Services;

/// <summary>
/// Keeps the on-chain fee snapshot fresh. Mirrors PriceWarmingService: warms all
/// networks once on startup, then refreshes on a fixed interval.
/// </summary>
public sealed class NetworkFeeWarmingService : BackgroundService
{
    private readonly INetworkFeeService _fees;
    private readonly ILogger<NetworkFeeWarmingService> _log;
    private readonly TimeSpan _interval;

    public NetworkFeeWarmingService(
        INetworkFeeService fees,
        ILogger<NetworkFeeWarmingService> log,
        IConfiguration config)
    {
        _fees = fees;
        _log = log;
        _interval = TimeSpan.FromSeconds(
            Math.Clamp(config.GetValue<int>("NetworkFee:WarmIntervalSeconds", 60), 15, 600));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[NetworkFeeWarming] Starting — interval={Interval}s, networks={Count}",
            _interval.TotalSeconds, _fees.Networks.Count);

        await WarmAllAsync(ct);

        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            await WarmAllAsync(ct);
    }

    private async Task WarmAllAsync(CancellationToken ct)
    {
        var tasks = _fees.Networks.Select(async network =>
        {
            try
            {
                await _fees.RefreshAsync(network, ct);
                _log.LogInformation("[NetworkFeeWarming] Refreshed {Network}", network);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[NetworkFeeWarming] Failed {Network}", network);
            }
        });
        await Task.WhenAll(tasks);
    }
}
