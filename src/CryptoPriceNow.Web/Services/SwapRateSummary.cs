using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CryptoPriceNow.Web.Services;

/// <summary>
/// "Best rate versus typical rate" over the stored quote history: for each pair and direction,
/// how far the typical exchange's quote trails the best one, and how each exchange compares.
/// Pure calculation over hourly per-exchange averages, so it can be tested without a database.
/// Published as swap-rates.json on the public API (see SwapRateSummaryPublisher).
/// </summary>
public static class SwapRateSummary
{
    /// <summary>An hour needs this many exchanges quoting before it counts.</summary>
    public const int MinExchanges = 4;

    /// <summary>A quote further than this from the hour's median is treated as a glitch and dropped.</summary>
    public const decimal GlitchFraction = 0.10m;

    /// <summary>"Near the best rate" means within this fraction of it.</summary>
    public const decimal NearBestFraction = 0.005m;

    /// <summary>An exchange that has not quoted in this many days is no longer tracked and is left out.</summary>
    public const int StaleAfterDays = 3;

    /// <summary>An exchange needs this many counted hours before it is listed.</summary>
    public const int MinExchangeHours = 24;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>One exchange's average buy and sell price for one pair in one hour (quote units per XMR).</summary>
    public sealed record HourQuote(string ExchangeKey, string ExchangeName, string Pair, DateTime HourUtc, decimal Buy, decimal Sell);

    public sealed record Document(string GeneratedUtc, int WindowDays, string RateType, int MinExchanges, decimal NearBestPct, string Source, List<Market> Markets);

    public sealed record Market(
        string Pair, string Quote, string Side, int Hours, decimal Depth, decimal EdgePct, [property: JsonPropertyName("edge_pct_7d")] decimal? EdgePct7d,
        List<Day> Daily, List<ExchangeScore> Exchanges);

    public sealed record Day(string D, decimal BestPrice, decimal MedianPrice, decimal EdgePct);

    public sealed record ExchangeScore(string Key, string Name, int Hours, decimal NearBestPct, decimal BestPct, decimal MedianGapPct);

    public static string ToJson(Document doc) => JsonSerializer.Serialize(doc, Json);

    /// <param name="quotes">Hourly averages, already limited to floating-rate quotes that pass the board's sanity rules.</param>
    /// <param name="nowUtc">The moment the summary is built for (the 7-day figure counts back from here).</param>
    /// <param name="windowDays">How many days the quotes cover.</param>
    public static Document Build(IEnumerable<HourQuote> quotes, DateTime nowUtc, int windowDays)
    {
        var markets = new List<Market>();
        foreach (var pairGroup in quotes.GroupBy(q => q.Pair).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var side in new[] { "buy", "sell" })
            {
                var market = BuildMarket(pairGroup.Key, side, pairGroup, nowUtc);
                if (market != null)
                {
                    markets.Add(market);
                }
            }
        }

        return new Document(
            nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            windowDays,
            "float",
            MinExchanges,
            NearBestFraction * 100m,
            "moneropricenow.com",
            markets);
    }

    /// <summary>"XMR/USDT:Tron" → "USDT".</summary>
    public static string QuoteLabel(string pair)
    {
        var quote = pair.Contains('/') ? pair[(pair.IndexOf('/') + 1)..] : pair;
        return quote.Contains(':') ? quote[..quote.IndexOf(':')] : quote;
    }

    private static Market? BuildMarket(string pair, string side, IEnumerable<HourQuote> quotes, DateTime nowUtc)
    {
        // "Value" = what the customer gets, so higher is always better:
        // buying XMR → XMR per 1 quote unit (1 / buy price); selling XMR → quote units per XMR.
        var hours = new List<(DateTime Hour, decimal Best, decimal Median, List<(string Key, string Name, decimal Value)> Rows)>();
        foreach (var hour in quotes.GroupBy(q => q.HourUtc))
        {
            var rows = hour
                .Where(q => q.Buy > 0 && q.Sell > 0)
                .Select(q => (q.ExchangeKey, q.ExchangeName, Value: side == "buy" ? 1m / q.Buy : q.Sell))
                .ToList();
            if (rows.Count < MinExchanges)
            {
                continue;
            }

            var roughMedian = Median(rows.Select(r => r.Value));
            rows = rows.Where(r => Math.Abs(r.Value - roughMedian) / roughMedian <= GlitchFraction).ToList();
            if (rows.Count < MinExchanges)
            {
                continue;
            }

            hours.Add((hour.Key, rows.Max(r => r.Value), Median(rows.Select(r => r.Value)), rows));
        }

        if (hours.Count == 0)
        {
            return null;
        }

        static decimal Edge(decimal best, decimal median) => (best - median) / best * 100m;
        decimal Price(decimal value) => side == "buy" ? 1m / value : value;

        var weekAgo = nowUtc.AddDays(-7);
        var lastWeek = hours.Where(h => h.Hour >= weekAgo).ToList();

        var daily = hours
            .GroupBy(h => h.Hour.Date)
            .OrderBy(g => g.Key)
            .Select(g => new Day(
                g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Sig(Price(Median(g.Select(h => h.Best)))),
                Sig(Price(Median(g.Select(h => h.Median)))),
                Math.Round(Median(g.Select(h => Edge(h.Best, h.Median))), 3)))
            .ToList();

        var recently = nowUtc.AddDays(-StaleAfterDays);
        var exchanges = hours
            .SelectMany(h => h.Rows.Select(r => (r.Key, r.Name, h.Hour, Gap: (h.Best - r.Value) / h.Best)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinExchangeHours && g.Max(x => x.Hour) >= recently)
            .Select(g => new ExchangeScore(
                g.Key,
                g.First().Name,
                g.Count(),
                Math.Round(g.Count(x => x.Gap <= NearBestFraction) * 100m / g.Count(), 1),
                Math.Round(g.Count(x => x.Gap == 0m) * 100m / g.Count(), 1),
                Math.Round(Median(g.Select(x => x.Gap)) * 100m, 3)))
            .OrderByDescending(e => e.NearBestPct)
            .ThenBy(e => e.MedianGapPct)
            .ToList();

        return new Market(
            pair,
            QuoteLabel(pair),
            side,
            hours.Count,
            Math.Round((decimal)hours.Average(h => h.Rows.Count), 1),
            Math.Round(Median(hours.Select(h => Edge(h.Best, h.Median))), 3),
            lastWeek.Count >= 24 ? Math.Round(Median(lastWeek.Select(h => Edge(h.Best, h.Median))), 3) : null,
            daily,
            exchanges);
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2m;
    }

    /// <summary>Six significant digits: enough for a chart, small in the file.</summary>
    private static decimal Sig(decimal v)
    {
        if (v == 0m)
        {
            return 0m;
        }

        var digits = (int)Math.Floor(Math.Log10((double)Math.Abs(v))) + 1;
        return Math.Round(v, Math.Clamp(6 - digits, 0, 12));
    }
}
