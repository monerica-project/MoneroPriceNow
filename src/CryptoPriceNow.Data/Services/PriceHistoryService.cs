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
        string pair, string? rangeKey, CancellationToken ct = default)
    {
        TryGetPreset(rangeKey, out var preset);
        var fromUtc = DateTimeOffset.UtcNow - preset.Range;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date_bin(@bucket, "TimestampUtc", TIMESTAMPTZ '2000-01-03') AS bucket,
                   AVG("Buy")   AS avg_buy,
                   AVG("Sell")  AS avg_sell,
                   COUNT(*)::int AS samples,
                   (SELECT MIN("TimestampUtc") FROM "PriceQuotes" WHERE "Pair" = @pair) AS oldest
            FROM "PriceQuotes"
            WHERE "Pair" = @pair
              AND "TimestampUtc" >= @from
            GROUP BY 1
            ORDER BY 1;
            """;
        cmd.Parameters.AddWithValue("bucket", preset.Bucket);
        cmd.Parameters.AddWithValue("pair", pair);
        cmd.Parameters.AddWithValue("from", fromUtc);

        var points = new List<HistoryPoint>();
        DateTimeOffset? oldest = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var bucket = reader.GetFieldValue<DateTime>(0);
                decimal? buy = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
                decimal? sell = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
                var samples = reader.GetInt32(3);

                if (oldest is null && !reader.IsDBNull(4))
                {
                    var dt = reader.GetFieldValue<DateTime>(4);
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
            minCmd.CommandText = "SELECT MIN(\"TimestampUtc\") FROM \"PriceQuotes\" WHERE \"Pair\" = @pair;";
            minCmd.Parameters.AddWithValue("pair", pair);
            var raw = await minCmd.ExecuteScalarAsync(ct);
            if (raw is DateTime dt)
                oldest = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        return new HistoryResult(pair, preset.Key, (int)preset.Bucket.TotalSeconds, points, oldest);
    }
}
