using System.Text;
using CryptoPriceNow.Data;
using Microsoft.EntityFrameworkCore;

namespace CryptoPriceNow.Web.Services;

/// <summary>
/// Every few hours works out the "best rate versus typical rate" summary from the stored quote
/// history (last 30 days, floating rates) and publishes it as swap-rates.json on the public API
/// (api.moneropricenow.com), next to the price files. Other sites read that file; nothing calls
/// this web server for it. Disabled when no storage key is configured.
/// </summary>
public sealed class SwapRateSummaryPublisher : BackgroundService
{
    public const string FileName = "swap-rates.json";
    public const int WindowDays = 30;

    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<PriceDbContext> _dbFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SwapRateSummaryPublisher> _log;
    private readonly bool _enabled;
    private readonly string _zone;
    private readonly string _key;
    private readonly string _host;
    private readonly TimeSpan _interval;

    public SwapRateSummaryPublisher(
        IDbContextFactory<PriceDbContext> dbFactory,
        IHttpClientFactory httpFactory,
        ILogger<SwapRateSummaryPublisher> log,
        IConfiguration config)
    {
        _dbFactory = dbFactory;
        _httpFactory = httpFactory;
        _log = log;
        _zone = config["Bunny:StorageZone"] ?? string.Empty;
        _key = config["Bunny:StorageKey"] ?? string.Empty;
        _host = config["Bunny:StorageHost"] ?? "storage.bunnycdn.com";
        _interval = TimeSpan.FromMinutes(Math.Clamp(config.GetValue<int>("Bunny:SwapRatesIntervalMinutes", 180), 30, 1440));
        _enabled = config.GetValue("Bunny:Enabled", true) && !string.IsNullOrWhiteSpace(_zone) && !string.IsNullOrWhiteSpace(_key);
    }

    /// <summary>Hourly per-exchange averages of the floating-rate quotes that pass the board's sanity rules.</summary>
    public static async Task<List<SwapRateSummary.HourQuote>> LoadAsync(PriceDbContext db, int windowDays, CancellationToken ct)
    {
        const string sql = """
            SELECT e."ExchangeKey", e."SiteName", q."Pair", date_trunc('hour', q."TimestampUtc") AS h,
                   avg(q."Buy") AS buy, avg(q."Sell") AS sell
            FROM "PriceQuotes" q
            JOIN "Exchanges" e ON e."Id" = q."ExchangeId"
            WHERE q."TimestampUtc" >= now() - make_interval(days => @days)
              AND q."RateType" = 'float'
              AND q."Buy" > 0 AND q."Sell" > 0 AND q."Buy" >= q."Sell"
              AND (q."Buy" - q."Sell") / q."Buy" <= 0.25
            GROUP BY 1, 2, 3, 4
            """;
        var rows = new List<SwapRateSummary.HourQuote>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 180;
        var p = cmd.CreateParameter();
        p.ParameterName = "days";
        p.Value = windowDays;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SwapRateSummary.HourQuote(
                reader.GetString(0),
                reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                reader.GetString(2),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                reader.GetDecimal(4),
                reader.GetDecimal(5)));
        }

        return rows;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled)
        {
            _log.LogInformation("[SwapRates] Disabled (no Bunny:StorageKey/StorageZone): not publishing.");
            return;
        }

        try
        {
            await Task.Delay(StartDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var quotes = await LoadAsync(db, WindowDays, ct);
                var doc = SwapRateSummary.Build(quotes, DateTime.UtcNow, WindowDays);
                if (doc.Markets.Count == 0)
                {
                    _log.LogWarning("[SwapRates] No market had enough quotes; the published file is left as it is.");
                    continue;
                }

                await PutAsync(SwapRateSummary.ToJson(doc), ct);
                _log.LogInformation("[SwapRates] Published {Markets} markets from {Rows} hourly rows.", doc.Markets.Count, quotes.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[SwapRates] Publish failed; it is tried again on the next run.");
            }
        }
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct));
    }

    private async Task PutAsync(string json, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"https://{_host}/{_zone}/{FileName}");
        req.Headers.Add("AccessKey", _key);
        req.Content = new StringContent(json, Encoding.UTF8);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("[SwapRates] PUT {File} -> {Status}", FileName, (int)resp.StatusCode);
        }
    }
}
