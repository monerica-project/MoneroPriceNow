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

// On-chain network/gas fees (BTC/ETH/XMR) — free public sources, no key needed.
builder.Services.Configure<NetworkFeeOptions>(builder.Configuration.GetSection("NetworkFee"));
builder.Services.AddSingleton<INetworkFeeService, NetworkFeeService>();
builder.Services.AddHostedService<NetworkFeeWarmingService>();

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

// Force lowercase URLs: 301-redirect any request whose PATH contains uppercase letters to
// the all-lowercase form (query string preserved verbatim — values like pair=XMR/USDT:Tron
// stay untouched). This keeps a single canonical casing per URL so mixed-case inbound links
// don't spawn duplicate pages. Static assets (paths with a file extension) are skipped.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    if (!string.IsNullOrEmpty(path)
        && !System.IO.Path.HasExtension(path)
        && path.Any(char.IsUpper))
    {
        context.Response.Redirect(path.ToLowerInvariant() + context.Request.QueryString, permanent: true);
        return;
    }

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

// Render a branded 404 (and other status codes) via the /NotFound page.
app.UseStatusCodePagesWithReExecute("/NotFound");

if (!disableHttpsRedirect)
{
    app.UseHttpsRedirection();
}
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

// XML sitemap, generated from the same PairCatalog single-source-of-truth the
// routes use (so a new pair shows up automatically) plus the static info pages.
app.MapGet("/sitemap.xml", async (HttpContext http, CancellationToken ct) =>
{
    const string origin = "https://moneropricenow.com";
    var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

    var entries = new List<(string Loc, string ChangeFreq, string Priority)>();
    // Live price pages — market-driven, so they change constantly.
    foreach (var p in PairCatalog.All)
        entries.Add((origin + p.Url, "hourly", string.IsNullOrEmpty(p.Slug) ? "1.0" : "0.9"));

    // The browsable exchange index and one page per active exchange (slug from its name).
    var directory = http.RequestServices.GetService<CryptoPriceNow.Data.Services.ExchangeDirectoryService>();
    if (directory is not null)
    {
        try
        {
            entries.Add(($"{origin}/exchanges", "daily", "0.8"));
            foreach (var e in await directory.GetActiveAsync(ct))
            {
                var slug = CryptoPriceNow.Data.Services.ExchangeSlug.From(e.SiteName);
                if (!string.IsNullOrEmpty(slug))
                    entries.Add(($"{origin}/exchange/{slug}", "hourly", "0.7"));
            }
        }
        catch { /* DB unavailable — ship the sitemap without exchange pages rather than 500. */ }
    }
    // Stable informational pages.
    foreach (var info in new[]
        {
            ("/network", "0.6"), ("/about", "0.5"),
            ("/sponsors", "0.4"), ("/contact", "0.4"), ("/privacy", "0.3"),
        })
        entries.Add((origin + info.Item1, "monthly", info.Item2));

    var sb = new System.Text.StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
    foreach (var (loc, changefreq, priority) in entries)
        sb.Append("  <url>\n")
          .Append($"    <loc>{loc}</loc>\n")
          .Append($"    <lastmod>{today}</lastmod>\n")
          .Append($"    <changefreq>{changefreq}</changefreq>\n")
          .Append($"    <priority>{priority}</priority>\n")
          .Append("  </url>\n");
    sb.Append("</urlset>\n");
    return Results.Content(sb.ToString(), "application/xml");
});

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

// ── Headline XMR price (/api/price/xmr-usdt.json) ─────────────────────────────
// A single market price for external consumers (e.g. monerouserforum.com). Reduces
// the live two-way exchange rows to one mid value (mean of all buy+sell quotes, the
// same "market" number the board's JS shows) and memoizes it for 15s so this is cheap
// and never hammers the exchanges. Shape: {"priceUsdt":164.23,"asOfUtc":"...","source":"..."}
var _xmrPriceCache = string.Empty;
var _xmrPriceCachedAt = DateTime.MinValue;
var _xmrPriceTtl = TimeSpan.FromSeconds(15);
var _xmrPriceLock = new SemaphoreSlim(1, 1);

app.MapGet("/api/price/xmr-usdt.json", async (
    [FromServices] IPriceService prices, HttpResponse response, CancellationToken ct) =>
{
    response.Headers["Cache-Control"] = "public, max-age=15";

    if (!string.IsNullOrEmpty(_xmrPriceCache) && DateTime.UtcNow - _xmrPriceCachedAt < _xmrPriceTtl)
    {
        return Results.Content(_xmrPriceCache, "application/json");
    }

    await _xmrPriceLock.WaitAsync(ct);
    try
    {
        if (!string.IsNullOrEmpty(_xmrPriceCache) && DateTime.UtcNow - _xmrPriceCachedAt < _xmrPriceTtl)
        {
            return Results.Content(_xmrPriceCache, "application/json");
        }

        var rows = await prices.GetTwoWayPricesAsync("XMR", "USDTTRC", ct);
        var vals = new List<decimal>();
        DateTimeOffset? maxTs = null;
        foreach (var r in rows)
        {
            if (r.Sell is decimal s && s > 0m) { vals.Add(s); }
            if (r.Buy is decimal b && b > 0m) { vals.Add(b); }
            if (r.TsUtc is DateTimeOffset t && (maxTs is null || t > maxTs)) { maxTs = t; }
        }

        string json;
        if (vals.Count == 0)
        {
            json = "{\"priceUsdt\":null,\"asOfUtc\":null,\"source\":\"moneropricenow.com\"}";
        }
        else
        {
            var mid = Math.Round(vals.Average(), 2, MidpointRounding.AwayFromZero);
            var asOf = (maxTs ?? DateTimeOffset.UtcNow).UtcDateTime;
            json = "{\"priceUsdt\":" + mid.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                 + ",\"asOfUtc\":\"" + asOf.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\""
                 + ",\"source\":\"moneropricenow.com\"}";
            _xmrPriceCache = json;
            _xmrPriceCachedAt = DateTime.UtcNow;
        }

        return Results.Content(json, "application/json");
    }
    catch
    {
        return Results.Content(
            string.IsNullOrEmpty(_xmrPriceCache) ? "{\"priceUsdt\":null,\"asOfUtc\":null,\"source\":\"moneropricenow.com\"}" : _xmrPriceCache,
            "application/json");
    }
    finally
    {
        _xmrPriceLock.Release();
    }
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
    string? rateType,
    string? exchange,
    CancellationToken ct) =>
{
    var history = http.RequestServices.GetService<PriceHistoryService>();
    if (history is null)
        return Results.Ok(new { enabled = false, points = Array.Empty<object>() });

    // Optional rate-type filter: "float" or "fixed". Anything else = all types (legacy chart).
    var rt = rateType?.Trim().ToLowerInvariant();
    if (rt is not ("float" or "fixed")) rt = null;

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

    // Optional single-exchange filter: /api/history?...&exchange=<key> charts just that
    // exchange (raw quotes binned on the fly) instead of the pooled all-exchange rollup.
    var exchangeKey = string.IsNullOrWhiteSpace(exchange) ? null : exchange.Trim();

    try
    {
        var result = exchangeKey is null
            ? await history.GetHistoryAsync(trackedPair.HistoryPair, range, rt, ct)
            : await history.GetExchangeHistoryAsync(exchangeKey, trackedPair.HistoryPair, range, rt, ct);

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
            rateType = rt ?? "all",
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

// Current network fee for a coin — polled by the live summary block so the
// fee values (and their change direction) update without a page reload.
app.MapGet("/api/network-fee", async (
    HttpContext http,
    string? network,
    CancellationToken ct) =>
{
    var svc = http.RequestServices.GetService<INetworkFeeService>();
    if (svc is null) return Results.Ok(new { ok = false });

    var requested = (network ?? "").Trim().ToLowerInvariant();
    var tracked = PairCatalog.All
        .Select(p => p.FeeNetwork)
        .FirstOrDefault(n => !string.IsNullOrEmpty(n) && n.Equals(requested, StringComparison.OrdinalIgnoreCase));
    if (tracked is null) return Results.BadRequest(new { error = "unknown network" });

    var fee = await svc.GetFeeAsync(tracked, ct);
    if (fee is null) return Results.Ok(new { ok = false });

    return Results.Ok(new
    {
        ok = true,
        network = fee.Network,
        title = fee.Title,
        note = fee.Note,
        updatedAtMs = fee.UpdatedAtUtc.ToUnixTimeMilliseconds(),
        tiers = fee.Tiers.Select(t => new
        {
            label = t.Label,
            primary = t.Primary,
            secondary = t.Secondary,
            usd = t.Usd
        })
    });
});

// Network-fee history (BTC/ETH/XMR) for the per-page fee chart.
app.MapGet("/api/fee-history", async (
    HttpContext http,
    string? network,
    string? range,
    CancellationToken ct) =>
{
    var history = http.RequestServices.GetService<NetworkFeeHistoryService>();
    if (history is null)
        return Results.Ok(new { enabled = false, points = Array.Empty<object>() });

    // Allow-list: only the networks our pages actually track.
    var requested = (network ?? "").Trim().ToLowerInvariant();
    var tracked = PairCatalog.All
        .Select(p => p.FeeNetwork)
        .FirstOrDefault(n => !string.IsNullOrEmpty(n) && n.Equals(requested, StringComparison.OrdinalIgnoreCase));
    if (tracked is null)
        return Results.BadRequest(new { error = "unknown network" });

    try
    {
        var result = await history.GetHistoryAsync(tracked, range, ct);

        var now = DateTimeOffset.UtcNow;
        var span = result.OldestUtc.HasValue ? now - result.OldestUtc.Value : TimeSpan.Zero;
        var presets = NetworkFeeHistoryService.Presets;
        var available = presets
            .Where((p, i) => i == 0 || span >= p.Range)
            .Select(p => p.Key)
            .ToArray();

        return Results.Ok(new
        {
            enabled = true,
            network = result.Network,
            range = result.RangeKey,
            bucketSeconds = result.BucketSeconds,
            oldestMs = result.OldestUtc?.ToUnixTimeMilliseconds(),
            availableRanges = available,
            points = result.Points.Select(p => new
            {
                t = p.BucketUtc.ToUnixTimeMilliseconds(),
                usd = p.AvgUsdPerTx,
                native = p.AvgNative,
                n = p.Samples
            })
        });
    }
    catch (OperationCanceledException) { throw; }
    catch
    {
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

// ── Monero payment invoice (/api/invoice) ────────────────────────────────────
// Returns an SVG image with a scannable monero: QR code for the given address + amount.
app.MapGet("/api/invoice", (string? address, decimal xmr, decimal? usd) =>
{
    address = (address ?? string.Empty).Trim();
    if (!CryptoPriceNow.Web.Services.InvoiceBuilder.IsValidAddress(address) || xmr <= 0m)
    {
        return Results.BadRequest("Invalid Monero address or amount.");
    }

    var svg = CryptoPriceNow.Web.Services.InvoiceBuilder.BuildSvg(
        address, xmr, usd, DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm"));
    return Results.Content(svg, "image/svg+xml");
});

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