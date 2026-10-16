using CryptoPriceNow.Web.Models;
using ExchangeServices.Interfaces;

namespace CryptoPriceNow.Services;

/// <summary>
/// The board's headline "mid" price, computed exactly like moneropricenow.js computeStats on the
/// default (Float) view: the mean of every buy AND sell quote (each ≥ the pair's minimum valid
/// price) across the FLOAT rows only. Fixed rows are a separate view on the board — including
/// them double-counts fixed-capable exchanges and skews the mid toward their (wider) fixed spread.
///
/// Callers pass rows from <see cref="IPriceService.GetTwoWayPricesAsync(string, string, CancellationToken)"/>,
/// which has already applied the IsSaneTwoWayRow filter (both sides, Buy ≥ Sell, spread cap) —
/// the same rows the board's /api/prices/two-way feed receives.
/// </summary>
public static class BoardMid
{
    public static bool IsFloat(TwoWayPriceRow r)
        => string.Equals(string.IsNullOrWhiteSpace(r.RateType) ? RateTypes.Float : r.RateType,
                         RateTypes.Float, StringComparison.OrdinalIgnoreCase);

    public static decimal? FloatMid(IEnumerable<TwoWayPriceRow> rows, decimal minValidPrice = 0m)
    {
        var floor = minValidPrice > 0m ? minValidPrice : decimal.MinValue;
        var vals = new List<decimal>();
        foreach (var r in rows.Where(IsFloat))
        {
            if (r.Buy is decimal b && b > 0m && b >= floor) vals.Add(b);
            if (r.Sell is decimal s && s > 0m && s >= floor) vals.Add(s);
        }

        return vals.Count > 0 ? vals.Average() : null;
    }
}
