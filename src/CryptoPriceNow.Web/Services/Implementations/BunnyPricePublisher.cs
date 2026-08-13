using System.Globalization;
using System.Text;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CryptoPriceNow.Web.Services;

/// <summary>
/// Publishes the public price API to Bunny Edge Storage every N seconds, so the
/// endpoint (api.moneropricenow.com) is served entirely by Bunny's CDN and never
/// hits this web server. One JSON file per pair (xmr-usdt.json, xmr-btc.json,
/// xmr-eth.json) plus a landing index.html listing them. Outbound only.
///
/// Each JSON: {"pair":"XMR/USDT","price":164.23,"published_utc":"2026-08-13T17:00:05Z","source":"moneropricenow.com"}
/// The price is the mid of all buy+sell quotes across exchanges — the same number
/// the board's headline shows. Disabled automatically when no storage key is set.
/// </summary>
public sealed class BunnyPricePublisher : BackgroundService
{
    private readonly IPriceService _prices;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BunnyPricePublisher> _log;

    private readonly bool _enabled;
    private readonly string _zone;
    private readonly string _key;
    private readonly string _host;
    private readonly TimeSpan _interval;

    public BunnyPricePublisher(
        IPriceService prices,
        IHttpClientFactory httpFactory,
        ILogger<BunnyPricePublisher> log,
        IConfiguration config)
    {
        _prices = prices;
        _httpFactory = httpFactory;
        _log = log;

        _zone = config["Bunny:StorageZone"] ?? string.Empty;
        _key = config["Bunny:StorageKey"] ?? string.Empty;
        _host = config["Bunny:StorageHost"] ?? "storage.bunnycdn.com";
        _interval = TimeSpan.FromSeconds(
            Math.Clamp(config.GetValue<int>("Bunny:PublishIntervalSeconds", 30), 10, 300));
        _enabled = config.GetValue("Bunny:Enabled", true)
                   && !string.IsNullOrWhiteSpace(_zone)
                   && !string.IsNullOrWhiteSpace(_key);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_enabled)
        {
            _log.LogInformation("[BunnyPublisher] Disabled (no Bunny:StorageKey/StorageZone) — not publishing.");
            return;
        }

        _log.LogInformation("[BunnyPublisher] Starting — zone={Zone}, interval={Interval}s", _zone, _interval.TotalSeconds);

        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await PublishAllAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[BunnyPublisher] Publish cycle failed");
            }
        }
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct));
    }

    private async Task PublishAllAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);

        var published = new List<(string Slug, string Pair, decimal? Price, string WhenUtc)>();

        foreach (var p in PairCatalog.All)
        {
            var slug = "xmr-" + p.QuoteLabel.ToLowerInvariant();     // xmr-usdt, xmr-btc, xmr-eth
            var pairLabel = $"XMR/{p.QuoteLabel}";
            var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            decimal? price = null;
            try
            {
                var rows = await _prices.GetTwoWayPricesAsync(p.Base, p.ApiQuote, ct);
                var vals = new List<decimal>();
                foreach (var r in rows)
                {
                    if (r.Sell is decimal s && s > 0m) { vals.Add(s); }
                    if (r.Buy is decimal b && b > 0m) { vals.Add(b); }
                }

                if (vals.Count > 0)
                {
                    price = Math.Round(vals.Average(), p.Decimals, MidpointRounding.AwayFromZero);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[BunnyPublisher] price failed for {Pair}", pairLabel);
            }

            var priceJson = price is decimal pv
                ? pv.ToString("0." + new string('0', p.Decimals), CultureInfo.InvariantCulture)
                : "null";
            var json = $"{{\"pair\":\"{pairLabel}\",\"price\":{priceJson},\"published_utc\":\"{nowUtc}\",\"source\":\"moneropricenow.com\"}}";

            // Only overwrite the live file when we have a real price; a transient exchange
            // hiccup should leave the last good value in place rather than publish null.
            if (price is not null)
            {
                await PutAsync(http, $"{slug}.json", json, "application/json", ct);
            }

            published.Add((slug, pairLabel, price, nowUtc));
        }

        await PutAsync(http, "index.html", BuildIndexHtml(published), "text/html; charset=utf-8", ct);
    }

    private async Task PutAsync(HttpClient http, string path, string body, string contentType, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"https://{_host}/{_zone}/{path}");
        req.Headers.Add("AccessKey", _key);
        req.Content = new StringContent(body, Encoding.UTF8);
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType.Split(';')[0].Trim())
        {
            CharSet = contentType.Contains("charset", StringComparison.OrdinalIgnoreCase) ? "utf-8" : null,
        };

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("[BunnyPublisher] PUT {Path} -> {Status}", path, (int)resp.StatusCode);
        }
    }

    private static string BuildIndexHtml(List<(string Slug, string Pair, decimal? Price, string WhenUtc)> pairs)
    {
        var rows = new StringBuilder();
        foreach (var p in pairs)
        {
            var priceTxt = p.Price is decimal v ? v.ToString(CultureInfo.InvariantCulture) : "—";
            rows.Append(
                $"<tr><td><a href=\"/{p.Slug}.json\">/{p.Slug}.json</a></td><td>{p.Pair}</td>" +
                $"<td class=\"n\">{priceTxt}</td></tr>");
        }

        var updated = pairs.Count > 0 ? pairs[0].WhenUtc : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        return $$"""
<!doctype html>
<html lang="en"><head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>MoneroPriceNow API — Public XMR Price Endpoints</title>
<style>
  :root { color-scheme: dark; }
  body { background:#0d1017; color:#e6e8ee; font:16px/1.6 system-ui,-apple-system,Segoe UI,Roboto,sans-serif; margin:0; padding:2.5rem 1.25rem; }
  main { max-width:760px; margin:0 auto; }
  h1 { font-size:1.7rem; margin:0 0 .25rem; }
  .sub { color:#9aa3b2; margin:0 0 1.75rem; }
  .bar { display:inline-block; width:52px; height:8px; background:#f0a020; border-radius:2px; margin-bottom:1rem; }
  table { width:100%; border-collapse:collapse; margin:1rem 0; }
  th,td { text-align:left; padding:.6rem .5rem; border-bottom:1px solid #232838; }
  th { color:#9aa3b2; font-weight:600; font-size:.85rem; text-transform:uppercase; letter-spacing:.04em; }
  td.n, th.n { text-align:right; font-variant-numeric:tabular-nums; }
  a { color:#f0a020; text-decoration:none; } a:hover { text-decoration:underline; }
  code,pre { background:#161b26; border:1px solid #232838; border-radius:6px; }
  code { padding:.1rem .4rem; } pre { padding:1rem; overflow:auto; }
  .note { color:#9aa3b2; font-size:.92rem; }
  footer { margin-top:2rem; color:#6b7385; font-size:.85rem; }
</style></head>
<body><main>
  <div class="bar"></div>
  <h1>MoneroPriceNow API</h1>
  <p class="sub">Free, public, no-auth Monero (XMR) price endpoints — served from the edge, updated every 30 seconds.</p>

  <table>
    <thead><tr><th>Endpoint</th><th>Pair</th><th class="n">Latest</th></tr></thead>
    <tbody>{{rows}}</tbody>
  </table>

  <p class="note">Each endpoint returns JSON:</p>
  <pre><code>{
  "pair": "XMR/USDT",
  "price": 164.23,
  "published_utc": "2026-08-13T17:00:05Z",
  "source": "moneropricenow.com"
}</code></pre>

  <p class="note">
    <strong>price</strong> is the mid of live buy + sell quotes across exchanges.
    <strong>published_utc</strong> is when this file was last written (UTC, to the second).
    Responses are edge-cached ~30s and send <code>Access-Control-Allow-Origin: *</code>, so browser apps can call them directly. No API key required.
  </p>

  <footer>Data by <a href="https://moneropricenow.com">moneropricenow.com</a> · last published {{updated}} UTC</footer>
</main></body></html>
""";
    }
}
