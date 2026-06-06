using ExchangeServices.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using ExchangeServices.Http;

namespace ExchangeServices.SageSwap;

public sealed class SageSwapClient : ISageSwapClient, IExchangeCurrencyApi
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // SageSwap quotes XMR<->USDT on ERC20 and SOL (NOT Tron). USDT is ~$1 on every
    // chain, so for a price feed any of these is a valid USDT/XMR figure. Preference
    // order; the board's USDT(TRC20) isn't offered for XMR so we fall back to ERC20.
    private static readonly string[] UsdtPref =
        { "USDTERC20", "USDTSOL", "USDTBSC", "USDTMATIC", "USDTARBITRUM", "USDTTRC20" };

    private readonly HttpClient http;
    private readonly SageSwapOptions opt;
    private readonly string ratesXmlUrl;

    // Short-lived cache so a sell+buy in the same refresh cycle = one feed fetch.
    private static readonly TimeSpan FeedTtl = TimeSpan.FromSeconds(12);
    private readonly SemaphoreSlim feedLock = new(1, 1);
    private List<RateItem>? feedItems;
    private DateTimeOffset feedAt;

    public string  ExchangeKey => "sageswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public SageSwapClient(HttpClient http, IOptions<SageSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
        // currencies.xml lives at the site root, not under /api — derive from the host.
        this.ratesXmlUrl = new Uri(new Uri(opt.BaseUrl), "/currencies.xml").ToString();
    }

    private sealed record RateItem(string From, string To, decimal In, decimal Out);

    // SELL: 1 XMR -> USDT.  Feed row from=XMR, to=USDT* ; price = out/in (USDT per XMR).
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var items = await GetFeedAsync(ct);
        var row = PickUsdtRow(items, fromXmr: true);
        if (row is null || row.In <= 0 || row.Out <= 0) return null;

        var usdtPerXmr = row.Out / row.In; // USDT received per 1 XMR sold
        if (usdtPerXmr <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, usdtPerXmr, DateTimeOffset.UtcNow);
    }

    // BUY: USDT -> 1 XMR.  Feed row from=USDT*, to=XMR ; price = in/out (USDT per XMR).
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var items = await GetFeedAsync(ct);
        var row = PickUsdtRow(items, fromXmr: false);
        if (row is null || row.In <= 0 || row.Out <= 0) return null;

        var usdtPerXmr = row.In / row.Out; // USDT paid per 1 XMR bought
        if (usdtPerXmr <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, usdtPerXmr, DateTimeOffset.UtcNow);
    }

    // Pick the best XMR<->USDT row by USDT-chain preference, else any XMR<->USDT* row.
    private static RateItem? PickUsdtRow(IReadOnlyList<RateItem> items, bool fromXmr)
    {
        foreach (var pref in UsdtPref)
        {
            var hit = items.FirstOrDefault(i => fromXmr
                ? i.From.Equals("XMR", StringComparison.OrdinalIgnoreCase) &&
                  i.To.Equals(pref, StringComparison.OrdinalIgnoreCase)
                : i.To.Equals("XMR", StringComparison.OrdinalIgnoreCase) &&
                  i.From.Equals(pref, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        return items.FirstOrDefault(i => fromXmr
            ? i.From.Equals("XMR", StringComparison.OrdinalIgnoreCase) &&
              i.To.StartsWith("USDT", StringComparison.OrdinalIgnoreCase)
            : i.To.Equals("XMR", StringComparison.OrdinalIgnoreCase) &&
              i.From.StartsWith("USDT", StringComparison.OrdinalIgnoreCase));
    }

    // =========================
    // RATES FEED (BestChange-standard currencies.xml), cached briefly.
    // =========================
    private async Task<IReadOnlyList<RateItem>> GetFeedAsync(CancellationToken ct)
    {
        if (feedItems is not null && DateTimeOffset.UtcNow - feedAt < FeedTtl)
            return feedItems;

        await feedLock.WaitAsync(ct);
        try
        {
            if (feedItems is not null && DateTimeOffset.UtcNow - feedAt < FeedTtl)
                return feedItems;

            using var req = new HttpRequestMessage(HttpMethod.Get, ratesXmlUrl);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                req.Headers.UserAgent.ParseAdd(opt.UserAgent);

            var res = await http.SendForStringWithTimeoutAsync(req, DefaultTimeout, ct);
            if (res is null ||
                res.StatusCode < HttpStatusCode.OK ||
                res.StatusCode >= HttpStatusCode.MultipleChoices)
            {
                if (res is not null)
                    Console.WriteLine($"[SAGESWAP] feed {(int)res.StatusCode} from {ratesXmlUrl}");
                return feedItems ?? (IReadOnlyList<RateItem>)Array.Empty<RateItem>();
            }

            var parsed = ParseFeed(res.Body);
            if (parsed.Count > 0)
            {
                feedItems = parsed;
                feedAt = DateTimeOffset.UtcNow;
            }
            return feedItems ?? (IReadOnlyList<RateItem>)Array.Empty<RateItem>();
        }
        catch (OperationCanceledException)
        {
            return feedItems ?? (IReadOnlyList<RateItem>)Array.Empty<RateItem>();
        }
        finally { feedLock.Release(); }
    }

    private static List<RateItem> ParseFeed(string xml)
    {
        var list = new List<RateItem>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var item in doc.Descendants("item"))
            {
                var from = (string?)item.Element("from");
                var to   = (string?)item.Element("to");
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) continue;

                if (!decimal.TryParse((string?)item.Element("in"),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var inn)) continue;
                if (!decimal.TryParse((string?)item.Element("out"),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var outt)) continue;

                list.Add(new RateItem(from.Trim(), to.Trim(), inn, outt));
            }
        }
        catch { /* return whatever parsed */ }
        return list;
    }

    // =========================
    // CURRENCIES — still from the JSON API (unchanged) for IExchangeCurrencyApi.
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/currencies");
        AddHeaders(req);

        var res = await http.SendForStringWithTimeoutAsync(req, DefaultTimeout, ct);
        if (res is null) return Array.Empty<ExchangeCurrency>();
        if (res.StatusCode < HttpStatusCode.OK || res.StatusCode >= HttpStatusCode.MultipleChoices)
            return Array.Empty<ExchangeCurrency>();

        CurrenciesResponse? dto;
        try { dto = JsonSerializer.Deserialize<CurrenciesResponse>(res.Body, JsonOpt); }
        catch { return Array.Empty<ExchangeCurrency>(); }

        if (dto?.Data is null || dto.Data.Count == 0)
            return Array.Empty<ExchangeCurrency>();

        return dto.Data
            .Select(x => new ExchangeCurrency(
                ExchangeId: x.FriendlyId,
                Ticker: x.Ticker,
                Network: x.Network?.Name ?? ""))
            .ToList();
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(opt.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.Token);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── DTOs (currencies) ─────────────────────────────────────────────────
    private sealed class CurrenciesResponse
    {
        public List<CurrencyDto> Data { get; set; } = new();
    }

    private sealed class CurrencyDto
    {
        public string FriendlyId { get; set; } = "";
        public string Ticker { get; set; } = "";
        public NetworkDto? Network { get; set; }
    }

    private sealed class NetworkDto
    {
        public string Name { get; set; } = "";
    }
}