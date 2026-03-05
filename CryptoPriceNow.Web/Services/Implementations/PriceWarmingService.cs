using CryptoPriceNow.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPriceNow.Web.Services;

public sealed class PriceWarmingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PriceWarmingService> _log;
    private readonly TimeSpan _interval;

    private static readonly (string Base, string Quote)[] Pairs =
    [
        ("XMR", "USDTTRC")
    ];

    public PriceWarmingService(
        IServiceScopeFactory scopeFactory,
        ILogger<PriceWarmingService> log,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _interval = TimeSpan.FromSeconds(
            Math.Clamp(config.GetValue<int>("PriceService:WarmIntervalSeconds", 15), 5, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[PriceWarming] Starting — interval={Interval}s", _interval.TotalSeconds);
        await WarmAllAsync(ct);

        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            await WarmAllAsync(ct);
    }

    private async Task WarmAllAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // Cast to concrete type so we can call ForceRefreshAllAsync.
        // This evicts the cache first, then fetches live data from exchanges.
        // Without this, GetTwoWayPricesAsync just returns cached data and never
        // calls the exchange APIs — prices would never actually update.
        var prices = scope.ServiceProvider.GetRequiredService<IPriceService>() as PriceService;
        if (prices is null)
        {
            _log.LogError("[PriceWarming] IPriceService is not PriceService — cannot force refresh");
            return;
        }

        foreach (var (b, q) in Pairs)
        {
            try
            {
                var baseRef = PriceService.ParseAssetPublic(b);
                var quoteRef = PriceService.ParseAssetPublic(q);
                await prices.ForceRefreshAllAsync(baseRef, quoteRef, ct);
                _log.LogInformation("[PriceWarming] Refreshed {Base}/{Quote}", b, q);
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PriceWarming] Failed {Base}/{Quote}", b, q);
            }
        }
    }
}
