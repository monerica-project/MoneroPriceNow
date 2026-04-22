using CryptoPriceNow.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPriceNow.Web.Services;

public sealed class PriceWarmingService : BackgroundService
{
    private readonly IPriceService _prices;
    private readonly ILogger<PriceWarmingService> _log;
    private readonly TimeSpan _interval;

    private static readonly (string Base, string Quote)[] Pairs =
    [
        ("XMR", "USDTTRC"),
    ];

    public PriceWarmingService(
        IPriceService prices,
        ILogger<PriceWarmingService> log,
        IConfiguration config)
    {
        _prices = prices;
        _log = log;
        _interval = TimeSpan.FromSeconds(
            Math.Clamp(config.GetValue<int>("PriceService:WarmIntervalSeconds", 15), 5, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("[PriceWarming] Starting — interval={Interval}s", _interval.TotalSeconds);

        // First warm on startup — populates latestRows before any request arrives
        await WarmAllAsync(ct);

        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
            await WarmAllAsync(ct);
    }

    private async Task WarmAllAsync(CancellationToken ct)
    {
        var prices = _prices as PriceService;
        if (prices is null)
        {
            _log.LogError("[PriceWarming] IPriceService is not PriceService — cannot refresh");
            return;
        }

        var tasks = Pairs.Select(async pair =>
        {
            var (b, q) = pair;
            try
            {
                var baseRef = PriceService.ParseAssetPublic(b);
                var quoteRef = PriceService.ParseAssetPublic(q);
                await prices.RefreshAndStoreAsync(baseRef, quoteRef, ct);
                _log.LogInformation("[PriceWarming] Refreshed {Base}/{Quote}", b, q);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[PriceWarming] Failed {Base}/{Quote}", b, q);
            }
        });

        await Task.WhenAll(tasks);
    }
}