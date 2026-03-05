using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();

// Registers exchange clients + options binding
builder.Services.AddCryptoPriceNowServices(builder.Configuration);

// ✅ Override IPriceService to singleton so the cache is shared across ALL requests.
// If AddCryptoPriceNowServices registered it as Scoped/Transient, this replaces it.
// A new PriceService per request = empty cache every time = always slow.
builder.Services.AddSingleton<IPriceService, PriceService>();

// Background warmer — fetches all prices on startup and every N seconds.
// Every HTTP request just reads from the in-memory cache and returns instantly.
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

app.MapGet("/debug/{exchange}/currencies", async (
    [FromServices] IPriceService prices,
    string exchange,
    CancellationToken ct) =>
{
    var list = await prices.GetCurrenciesAsync(exchange, ct);
    return Results.Ok(list);
});

app.Run();