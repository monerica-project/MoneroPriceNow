using CryptoPriceNow.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CryptoPriceNow.Data.Services;

/// <summary>
/// Reads bucketed network-fee averages from PostgreSQL using date_bin(), so a
/// long-range chart returns one row per bucket instead of every sample.
/// </summary>
public sealed class NetworkFeeHistoryService
{
    private readonly IDbContextFactory<PriceDbContext> _dbFactory;

    public NetworkFeeHistoryService(IDbContextFactory<PriceDbContext> dbFactory)
        => _dbFactory = dbFactory;

    // Same presets as the price chart so the UI behaves consistently.
    public static readonly IReadOnlyList<(string Key, TimeSpan Range, TimeSpan Bucket)> Presets =
    [
        ("1h",  TimeSpan.FromHours(1),  TimeSpan.FromMinutes(1)),
        ("4h",  TimeSpan.FromHours(4),  TimeSpan.FromMinutes(2)),
        ("12h", TimeSpan.FromHours(12), TimeSpan.FromMinutes(10)),
        ("1d",  TimeSpan.FromDays(1),   TimeSpan.FromMinutes(15)),
        ("3d",  TimeSpan.FromDays(3),   TimeSpan.FromHours(1)),
        ("7d",  TimeSpan.FromDays(7),   TimeSpan.FromHours(2)),
        ("30d", TimeSpan.FromDays(30),  TimeSpan.FromHours(8)),
    ];

    public static bool TryGetPreset(string? key, out (string Key, TimeSpan Range, TimeSpan Bucket) preset)
    {
        foreach (var p in Presets)
            if (p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) { preset = p; return true; }
        preset = Presets[0];
        return false;
    }

    public async Task<NetworkFeeHistoryResult> GetHistoryAsync(
        string network, string? rangeKey, CancellationToken ct = default)
    {
        TryGetPreset(rangeKey, out var preset);
        var fromUtc = DateTimeOffset.UtcNow - preset.Range;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date_bin(@bucket, "TimestampUtc", TIMESTAMPTZ '2000-01-03') AS bucket,
                   AVG("UsdPerTx") AS avg_usd,
                   AVG("Native")   AS avg_native,
                   COUNT(*)::int   AS samples,
                   (SELECT MIN("TimestampUtc") FROM "NetworkFeeQuotes" WHERE "Network" = @network) AS oldest
            FROM "NetworkFeeQuotes"
            WHERE "Network" = @network
              AND "TimestampUtc" >= @from
            GROUP BY 1
            ORDER BY 1;
            """;
        cmd.Parameters.AddWithValue("bucket", preset.Bucket);
        cmd.Parameters.AddWithValue("network", network);
        cmd.Parameters.AddWithValue("from", fromUtc);

        var points = new List<NetworkFeeHistoryPoint>();
        DateTimeOffset? oldest = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var bucket = reader.GetFieldValue<DateTime>(0);
                decimal? usd = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
                decimal? native = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
                var samples = reader.GetInt32(3);

                if (oldest is null && !reader.IsDBNull(4))
                {
                    var dt = reader.GetFieldValue<DateTime>(4);
                    oldest = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                }

                points.Add(new NetworkFeeHistoryPoint(
                    new DateTimeOffset(DateTime.SpecifyKind(bucket, DateTimeKind.Utc)),
                    usd, native, samples));
            }
        }

        if (oldest is null)
        {
            await using var minCmd = conn.CreateCommand();
            minCmd.CommandText = "SELECT MIN(\"TimestampUtc\") FROM \"NetworkFeeQuotes\" WHERE \"Network\" = @network;";
            minCmd.Parameters.AddWithValue("network", network);
            var raw = await minCmd.ExecuteScalarAsync(ct);
            if (raw is DateTime dt)
                oldest = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        }

        return new NetworkFeeHistoryResult(network, preset.Key, (int)preset.Bucket.TotalSeconds, points, oldest);
    }
}
