using CryptoPriceNow.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CryptoPriceNow.Data.Services;

/// <summary>
/// Reads bucketed buy/sell/market averages straight from PostgreSQL using
/// date_bin(), so a 7-day chart never streams a million rows through EF —
/// the server returns one row per bucket.
/// </summary>
public sealed class PriceHistoryService
{
    private readonly IDbContextFactory<PriceDbContext> _dbFactory;

    public PriceHistoryService(IDbContextFactory<PriceDbContext> dbFactory)
        => _dbFactory = dbFactory;

    /// <summary>
    /// Range presets exposed to the UI. Key → (lookback window, bucket size).
    /// Live view buckets at 30s to match the logger's default interval.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, TimeSpan Range, TimeSpan Bucket)> Presets =
    [
        ("1h",  TimeSpan.FromHours(1),    TimeSpan.FromMinutes(1)),
        ("4h",  TimeSpan.FromHours(4),    TimeSpan.FromMinutes(2)),
        ("12h", TimeSpan.FromHours(12),   TimeSpan.FromMinutes(10)),
        ("1d",  TimeSpan.FromDays(1),     TimeSpan.FromMinutes(15)),
        ("3d",  TimeSpan.FromDays(3),     TimeSpan.FromHours(1)),
        ("7d",  TimeSpan.FromDays(7),     TimeSpan.FromHours(2)),
        ("30d", TimeSpan.FromDays(30),    TimeSpan.FromHours(8)),
        ("90d", TimeSpan.FromDays(90),    TimeSpan.FromDays(1)),
    ];

    public static bool TryGetPreset(string? key, out (string Key, TimeSpan Range, TimeSpan Bucket) preset)
    {
        foreach (var p in Presets)
        {
            if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                preset = p;
                return true;
            }
        }
        preset = Presets[0]; // default: 1h
        return false;
    }

    public async Task<HistoryResult> GetHistoryAsync(
        string pair, string? rangeKey, string? rateType = null, CancellationToken ct = default)
    {
        TryGetPreset(rangeKey, out var preset);
        var fromUtc = DateTimeOffset.UtcNow - preset.Range;

        // Optional rate-type filter ("float" | "fixed"). Null/empty = all rate types (legacy
        // behaviour). The clause is a constant string; the value is passed as a parameter.
        var rateFilter = string.IsNullOrEmpty(rateType) ? string.Empty : " AND \"RateType\" = @rateType";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        // Reads from the pre-aggregated 1-minute PriceBuckets rollup, re-bucketing to the
        // preset size. Sums + separate buy/sell counts give exact weighted averages, and
        // the rollup is tiny (a 30-day window is a few thousand rows, not millions), so
        // even the widest range returns in milliseconds.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT date_bin(@bucket, "Bucket", TIMESTAMPTZ '2000-01-03') AS bucket,
                   SUM("SumBuy")    AS sum_buy,
                   SUM("BuyCount")  AS buy_count,
                   SUM("SumSell")   AS sum_sell,
                   SUM("SellCount") AS sell_count,
                   SUM("Samples")::int AS samples,
                   (SELECT MIN("Bucket") FROM "PriceBuckets" WHERE "Pair" = @pair{rateFilter}) AS oldest
            FROM "PriceBuckets"
            WHERE "Pair" = @pair
              AND "Bucket" >= @from{rateFilter}
            GROUP BY 1
            ORDER BY 1;
            """;
        cmd.Parameters.AddWithValue("bucket", preset.Bucket);
        cmd.Parameters.AddWithValue("pair", pair);
        cmd.Parameters.AddWithValue("from", fromUtc);
        if (!string.IsNullOrEmpty(rateType))
            cmd.Parameters.AddWithValue("rateType", rateType);

        var points = new List<HistoryPoint>();
        DateTimeOffset? oldest = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var bucket = reader.GetFieldValue<DateTime>(0);
                var buyCount = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                var sellCount = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
                decimal? buy = buyCount > 0 && !reader.IsDBNull(1) ? reader.GetDecimal(1) / buyCount : null;
                decimal? sell = sellCount > 0 && !reader.IsDBNull(3) ? reader.GetDecimal(3) / sellCount : null;
                var samples = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);

                if (oldest is null && !reader.IsDBNull(6))
                {
                    var dt = reader.GetFieldValue<DateTime>(6);
                    oldest = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                }

                decimal? market = (buy, sell) switch
                {
                    (not null, not null) => (buy + sell) / 2m,
                    (not null, null) => buy,
                    (null, not null) => sell,
                    _ => null
                };

                points.Add(new HistoryPoint(
                    new DateTimeOffset(DateTime.SpecifyKind(bucket, DateTimeKind.Utc)),
                    buy, sell, market, samples));
            }
        }

        // If there are zero buckets in the window the query returns no rows, so
        // oldest stays null — fetch it on its own (reader is now closed).
        if (oldest is null)
        {
            await using var minCmd = conn.CreateCommand();
            minCmd.CommandText = $"SELECT MIN(\"Bucket\") FROM \"PriceBuckets\" WHERE \"Pair\" = @pair{rateFilter};";
            minCmd.Parameters.AddWithValue("pair", pair);
            if (!string.IsNullOrEmpty(rateType))
                minCmd.Parameters.AddWithValue("rateType", rateType);
            var raw = await minCmd.ExecuteScalarAsync(ct);
            if (raw is DateTime dt)
                oldest = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        return new HistoryResult(pair, preset.Key, (int)preset.Bucket.TotalSeconds, points, oldest);
    }

    /// <summary>
    /// Same shape as <see cref="GetHistoryAsync"/> but for a SINGLE exchange. The pooled
    /// PriceBuckets rollup isn't keyed by exchange, so this bins the raw PriceQuotes for
    /// one exchange+pair with date_bin() at query time. A single exchange's quote stream
    /// is a small fraction of the pooled table, so even a 30-day window stays cheap.
    /// </summary>
    public async Task<HistoryResult> GetExchangeHistoryAsync(
        string exchangeKey, string pair, string? rangeKey, string? rateType = null, CancellationToken ct = default)
    {
        TryGetPreset(rangeKey, out var preset);
        var fromUtc = DateTimeOffset.UtcNow - preset.Range;
        var rateFilter = string.IsNullOrEmpty(rateType) ? string.Empty : " AND \"RateType\" = @rateType";

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        // Resolve the exchange key to its id ONCE (unique-indexed lookup), then filter
        // PriceQuotes by ExchangeId directly. Joining Exchanges into the main aggregate and
        // the "oldest" subquery blocked the composite index: the oldest MIN became a 137k-row
        // scan instead of an O(log n) index probe. Filtering by id keeps both index-tight.
        await using (var idCmd = conn.CreateCommand())
        {
            idCmd.CommandText = "SELECT \"Id\" FROM \"Exchanges\" WHERE \"ExchangeKey\" = @key LIMIT 1;";
            idCmd.Parameters.AddWithValue("key", exchangeKey);
            var idObj = await idCmd.ExecuteScalarAsync(ct);
            if (idObj is not int exchangeId)
            {
                return new HistoryResult(pair, preset.Key, (int)preset.Bucket.TotalSeconds, new List<HistoryPoint>(), null);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT date_bin(@bucket, "TimestampUtc", TIMESTAMPTZ '2000-01-03') AS bucket,
                       -- ROUND the per-bucket sums: raw USDT quotes carry ~15+ fractional digits,
                       -- so an un-rounded SUM over a bucket can exceed 28-29 significant digits and
                       -- overflow .NET System.Decimal on GetDecimal() (throwing → chart shows as
                       -- "no data"). 8 dp is far more precision than any pair's average needs.
                       ROUND(SUM("Buy")  FILTER (WHERE "Buy"  IS NOT NULL), 8) AS sum_buy,
                       COUNT("Buy")  AS buy_count,
                       ROUND(SUM("Sell") FILTER (WHERE "Sell" IS NOT NULL), 8) AS sum_sell,
                       COUNT("Sell") AS sell_count,
                       COUNT(*)::int   AS samples,
                       (SELECT MIN("TimestampUtc") FROM "PriceQuotes"
                          WHERE "ExchangeId" = @eid AND "Pair" = @pair) AS oldest
                FROM "PriceQuotes"
                WHERE "ExchangeId" = @eid
                  AND "Pair" = @pair
                  AND "TimestampUtc" >= @from{rateFilter}
                GROUP BY 1
                ORDER BY 1;
                """;
            cmd.Parameters.AddWithValue("bucket", preset.Bucket);
            cmd.Parameters.AddWithValue("eid", exchangeId);
            cmd.Parameters.AddWithValue("pair", pair);
            cmd.Parameters.AddWithValue("from", fromUtc);
            if (!string.IsNullOrEmpty(rateType))
                cmd.Parameters.AddWithValue("rateType", rateType);

            return await ReadExchangeHistoryAsync(cmd, pair, preset, ct);
        }
    }

    // Runs a prepared exchange-history command and maps its rows to the shared HistoryResult
    // shape (kept separate so the query above stays focused on just building the SQL).
    private static async Task<HistoryResult> ReadExchangeHistoryAsync(
        NpgsqlCommand cmd, string pair, (string Key, TimeSpan Range, TimeSpan Bucket) preset, CancellationToken ct)
    {

        var points = new List<HistoryPoint>();
        DateTimeOffset? oldest = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var bucket = reader.GetFieldValue<DateTime>(0);
                var buyCount = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                var sellCount = reader.IsDBNull(4) ? 0L : reader.GetInt64(4);
                decimal? buy = buyCount > 0 && !reader.IsDBNull(1) ? reader.GetDecimal(1) / buyCount : null;
                decimal? sell = sellCount > 0 && !reader.IsDBNull(3) ? reader.GetDecimal(3) / sellCount : null;
                var samples = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);

                if (oldest is null && !reader.IsDBNull(6))
                    oldest = new DateTimeOffset(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(6), DateTimeKind.Utc));

                decimal? market = (buy, sell) switch
                {
                    (not null, not null) => (buy + sell) / 2m,
                    (not null, null) => buy,
                    (null, not null) => sell,
                    _ => null
                };

                points.Add(new HistoryPoint(
                    new DateTimeOffset(DateTime.SpecifyKind(bucket, DateTimeKind.Utc)), buy, sell, market, samples));
            }
        }

        return new HistoryResult(pair, preset.Key, (int)preset.Bucket.TotalSeconds, points, oldest);
    }
}
