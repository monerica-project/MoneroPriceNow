using CryptoPriceNow.Data;
using CryptoPriceNow.Data.Services;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using CryptoPriceNow.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Registers exchange clients + options binding
builder.Services.AddCryptoPriceNowServices(builder.Configuration);

// Postgres quote store. No-ops (NullPriceQuoteSink) when ConnectionStrings:PriceDb
// is absent, so the site runs unchanged without a database.
builder.Services.AddCryptoPriceNowData(builder.Configuration);

// Override IPriceService to singleton so the cache is shared across ALL requests.
builder.Services.AddSingleton<IPriceService, PriceService>();

// Background warmer — fetches all prices on startup and every N seconds.
builder.Services.AddHostedService<PriceWarmingService>();

var app = builder.Build();

// ── Database migration on startup ────────────────────────────────────────────
// Applies any pending EF migrations before the site starts serving. New deploys
// with new migrations self-upgrade the schema. Disable with
// Database:MigrateOnStartup=false if you ever want to run migrations manually.
var priceDbConfigured = !string.IsNullOrWhiteSpace(
    builder.Configuration.GetConnectionString("PriceDb"));

if (priceDbConfigured && builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PriceDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    try
    {
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("[PriceDb] Migrations applied / schema up to date");
    }
    catch (Exception ex)
    {
        // Don't kill the site if Postgres is down — quotes just won't log and
        // the chart will show as unavailable until the DB comes back.
        app.Logger.LogError(ex, "[PriceDb] Migration failed — price logging disabled until DB is reachable");
    }
}

var torUrl = builder.Configuration.GetValue<string>("TorUrl") ?? string.Empty;

// Remove X-Powered-By and other identifying headers
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.Remove("X-Powered-By");
        context.Response.Headers.Remove("X-AspNet-Version");
        context.Response.Headers.Remove("X-AspNetMvc-Version");
        context.Response.Headers.Remove("Server");

        // Advertise .onion version to Tor Browser on clearnet responses only.
        // Tor Browser reads Onion-Location and shows a ".onion available" pill.
        if (!string.IsNullOrEmpty(torUrl) && !context.Request.Host.Host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Onion-Location"] = torUrl + context.Request.Path + context.Request.QueryString;
        }

        return Task.CompletedTask;
    });
    await next();
});

var disableHttpsRedirect = builder.Configuration.GetValue<bool>("DisableHttpsRedirect");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    if (!disableHttpsRedirect)
    {
        app.UseHsts();
    }
}

if (!disableHttpsRedirect)
{
    app.UseHttpsRedirection();
}
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.MapGet("/api/prices", async (
    [FromServices] IPriceService prices,
    string @base,
    string quote,
    CancellationToken ct) =>
{
    var results = await prices.GetPricesAsync(@base, quote, ct);
    return Results.Ok(results.Select(r => new
    {
        exchange = r.Exchange,
        pair = $"{r.Base.Ticker}/{r.Quote.Ticker}",
        price = r.Price,
        tsUtc = r.TimestampUtc
    }));
});

app.MapGet("/api/prices/two-way", async (
    [FromServices] IPriceService prices,
    string @base,
    string quote,
    CancellationToken ct) =>
{
    var rows = await prices.GetTwoWayPricesAsync(@base, quote, ct);
    return Results.Ok(rows);
});

// ── Price history (/api/history) ─────────────────────────────────────────────
// Bucketed buy/sell/market averages for the chart. range = one of the presets
// in PriceHistoryService.Presets (30m, 1h, 3h, 6h, 12h, 24h, 3d, 7d).
// Returns { enabled:false } when no database is configured so the front-end
// hides the chart section cleanly.
app.MapGet("/api/history", async (
    HttpContext http,
    string? pair,
    string? range,
    CancellationToken ct) =>
{
    var history = http.RequestServices.GetService<PriceHistoryService>();
    if (history is null)
        return Results.Ok(new { enabled = false, points = Array.Empty<object>() });

    // Only allow pairs the warmer actually tracks — prevents arbitrary-string
    // queries against the table. The allow-list is the catalog itself, so any
    // pair added in PairCatalog.All is queryable here automatically.
    var requestedPair = string.IsNullOrWhiteSpace(pair)
        ? PairCatalog.Usdt.HistoryPair
        : pair.Trim();

    var trackedPair = PairCatalog.All
        .FirstOrDefault(p => p.HistoryPair.Equals(requestedPair, StringComparison.OrdinalIgnoreCase));
    if (trackedPair is null)
        return Results.BadRequest(new { error = "unknown pair" });

    try
    {
        var result = await history.GetHistoryAsync(trackedPair.HistoryPair, range, ct);

        // Which range presets have enough history behind them to be worth showing?
        // A range is available once data spans at least that far back. The shortest
        // preset is always available so there's never an empty range bar.
        var now = DateTimeOffset.UtcNow;
        var span = result.OldestUtc.HasValue ? now - result.OldestUtc.Value : TimeSpan.Zero;
        var presets = PriceHistoryService.Presets;
        var available = presets
            .Where((p, i) => i == 0 || span >= p.Range)
            .Select(p => p.Key)
            .ToArray();

        return Results.Ok(new
        {
            enabled = true,
            pair = result.Pair,
            range = result.RangeKey,
            bucketSeconds = result.BucketSeconds,
            oldestMs = result.OldestUtc?.ToUnixTimeMilliseconds(),
            availableRanges = available,
            points = result.Points.Select(p => new
            {
                t = p.BucketUtc.ToUnixTimeMilliseconds(),
                buy = p.AvgBuy,
                sell = p.AvgSell,
                market = p.Market,
                n = p.Samples
            })
        });
    }
    catch (OperationCanceledException) { throw; }
    catch
    {
        // DB temporarily unreachable — degrade gracefully, don't 500 the chart.
        return Results.Ok(new { enabled = false, points = Array.Empty<object>() });
    }
});

// ── Sponsor proxy (/api/sponsors) ────────────────────────────────────────────
// Fetches the active sponsor list from Monerica server-side (avoids CORS),
// caches for a configurable TTL, served to the browser as same-origin JSON.
// Config keys (appsettings.json):
//   Sponsors:SourceUrl        — upstream JSON endpoint
//   Sponsors:CacheTtlMinutes  — how long to cache the response (default: 5)
var _sponsorCache = string.Empty;
var _sponsorCachedAt = DateTime.MinValue;
var _sponsorCacheTtl = TimeSpan.FromMinutes(
    builder.Configuration.GetValue<int>("Sponsors:CacheTtlMinutes", 5));
var _sponsorLock = new SemaphoreSlim(1, 1);

app.MapGet("/api/sponsors", async (IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(_sponsorCache) && DateTime.UtcNow - _sponsorCachedAt < _sponsorCacheTtl)
        return Results.Content(_sponsorCache, "application/json");

    await _sponsorLock.WaitAsync(ct);
    try
    {
        if (!string.IsNullOrEmpty(_sponsorCache) && DateTime.UtcNow - _sponsorCachedAt < _sponsorCacheTtl)
            return Results.Content(_sponsorCache, "application/json");

        var sponsorUrl = app.Configuration["Sponsors:SourceUrl"];

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        var json = await client.GetStringAsync(sponsorUrl, ct);
        _sponsorCache = json;
        _sponsorCachedAt = DateTime.UtcNow;
        return Results.Content(json, "application/json");
    }
    catch
    {
        return Results.Content(string.IsNullOrEmpty(_sponsorCache) ? "[]" : _sponsorCache, "application/json");
    }
    finally
    {
        _sponsorLock.Release();
    }
});

app.MapGet("/debug/{exchange}/currencies", async (
    [FromServices] IPriceService prices,
    string exchange,
    CancellationToken ct) =>
{
    var list = await prices.GetCurrenciesAsync(exchange, ct);
    return Results.Ok(list);
});

app.Run();