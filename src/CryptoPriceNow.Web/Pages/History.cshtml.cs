using CryptoPriceNow.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>/history: the Monero price day by day, from the same stored series the charts use.</summary>
public sealed class HistoryModel : PageModel
{
    private readonly MillionaireService _price;

    public HistoryModel(MillionaireService price) => _price = price;

    public sealed record Day(DateOnly Date, decimal Price);

    public decimal? Now { get; private set; }
    public IReadOnlyList<MillionaireService.Comparison> Comparisons { get; private set; } = [];
    public IReadOnlyList<Day> Days { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var snap = await _price.GetAsync(ct);
        Now = snap.Price;
        Comparisons = snap.Comparisons;
        Days = ToDays(snap.Series);
    }

    /// <summary>The last stored price of each UTC day, newest first.</summary>
    public static IReadOnlyList<Day> ToDays(IReadOnlyList<MillionaireService.SeriesPoint> series) =>
        series
            .Where(p => p.Xmr > 0m)
            .GroupBy(p => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(p.T).UtcDateTime))
            .Select(g => new Day(g.Key, Math.Round(MillionaireService.TargetUsdt / g.OrderBy(p => p.T).Last().Xmr, 2)))
            .OrderByDescending(d => d.Date)
            .ToList();
}
