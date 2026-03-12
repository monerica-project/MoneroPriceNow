using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

// Registers exchange clients + options binding
builder.Services.AddCryptoPriceNowServices(builder.Configuration);

// Override IPriceService to singleton so the cache is shared across ALL requests.
builder.Services.AddSingleton<IPriceService, PriceService>();

// Background warmer — fetches all prices on startup and every N seconds.
builder.Services.AddHostedService<PriceWarmingService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
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