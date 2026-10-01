using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace CryptoPriceNow.Web.Support;

/// <summary>One exchange's LIVE quote for the exact amount the user asked for, already enriched
/// with sponsor status and the links the page needs.</summary>
public sealed record SwapQuoteRow(
    string ExchangeKey,
    string SiteName,
    decimal Receive,        // amount actually received in the "to" asset
    decimal PerXmr,         // asset units per 1 XMR (for the rate column)
    char? PrivacyLevel,
    string RateType,
    bool IsSponsor,
    string? OutHref,        // sponsor → direct link; else the exchange's affiliate/site URL
    string ExchangeUrl,     // on-site /exchange/<slug> prices & chart page
    decimal? MinAmountUsd);

/// <summary>
/// Amount-accurate swap quoting for /swap. Unlike the price board (which probes a fixed ~$2,500
/// once and caches a per-unit rate), this fans out to every exchange with the EXACT amount the
/// user entered — a real rate for 0.00001 BTC is nothing like the rate for 10 BTC, and an amount
/// below an exchange's minimum simply returns nothing and drops out. One side of the trade is
/// always XMR: selling XMR calls the sell API, buying XMR calls the buy API. Results are streamed
/// as they arrive so the page can render them one by one.
/// </summary>
public sealed class SwapQuoteService
{
    private readonly List<IExchangePriceApi> priceApis;
    private readonly List<IExchangeCurrencyApi> currencyApis;
    private readonly IMemoryCache cache;
    private readonly IHttpClientFactory httpFactory;
    private readonly IConfiguration config;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan CurrenciesTtl = TimeSpan.FromMinutes(60);

    // Exchanges whose client returns a genuine fixed-rate quote when PriceQuery.Fixed is set. In
    // fixed mode we only query these (others would just echo their float rate).
    private static readonly HashSet<string> FixedCapableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "changenow", "fixedfloat", "exolix", "stealthex", "simpleswap", "trocador", "letsexchange",
        "0trace", "swapuz", "changee", "swapgate", "bitania", "quickex", "pegasusswap", "sageswap",
        "swapzone", "elcapo", "explace", "ccecash", "etzswap", "alfacash", "flashift",
    };

    public SwapQuoteService(
        IEnumerable<IExchangePriceApi> priceApis,
        IEnumerable<IExchangeCurrencyApi> currencyApis,
        IMemoryCache cache,
        IHttpClientFactory httpFactory,
        IConfiguration config)
    {
        this.priceApis = priceApis.ToList();
        this.currencyApis = currencyApis.ToList();
        this.cache = cache;
        this.httpFactory = httpFactory;
        this.config = config;
    }

    public int ExchangeCount => this.priceApis.Count;

    private static bool IsPrivacyAllowed(char? grade)
    {
        if (grade is null)
        {
            return false;
        }

        var g = char.ToUpperInvariant(grade.Value);
        return g == 'V' || (g is >= 'A' and <= 'C');
    }

    /// <summary>
    /// Streams each exchange's quote for <paramref name="fromTicker"/> → <paramref name="toTicker"/>
    /// (one of which must be XMR) at the given amount, fastest-first, already enriched with sponsor
    /// status + links. A single failing/slow exchange never blocks the rest.
    /// </summary>
    public async IAsyncEnumerable<SwapQuoteRow> GetQuotesStream(
        string fromTicker, string toTicker, decimal amount, bool fixedRate = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        fromTicker = (fromTicker ?? string.Empty).Trim().ToLowerInvariant();
        toTicker = (toTicker ?? string.Empty).Trim().ToLowerInvariant();
        if (amount <= 0 || fromTicker.Length == 0 || toTicker.Length == 0 || fromTicker == toTicker)
        {
            yield break;
        }

        var sellingXmr = fromTicker == "xmr";
        var asset = sellingXmr ? toTicker : fromTicker;
        if (asset == "xmr" || (!sellingXmr && toTicker != "xmr"))
        {
            yield break; // exactly one side must be XMR
        }

        // Parse the pair the SAME way the board does so each client receives the ticker + network
        // it needs (e.g. "USDTTRC" → ticker USDT on Tron) — otherwise ticker-mapping clients (like
        // xmr2cex) can't resolve the asset and silently return nothing.
        var baseRef = CryptoPriceNow.Services.PriceService.ParseAssetPublic("XMR");
        var quoteRef = CryptoPriceNow.Services.PriceService.ParseAssetPublic(
            asset == "usdt" ? "USDTTRC" : asset.ToUpperInvariant());

        var sponsors = await GetSponsorsAsync(ct);

        var tasks = this.priceApis
            .Select(api => FetchOneAsync(api, baseRef, quoteRef, amount, sellingXmr, fixedRate, sponsors, ct))
            .ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);

            SwapQuoteRow? row = null;
            try
            {
                row = await done;
            }
            catch
            {
                // one exchange failing must not stop the stream
            }

            if (row is not null && row.Receive > 0)
            {
                yield return row;
            }
        }
    }

    private async Task<SwapQuoteRow?> FetchOneAsync(
        IExchangePriceApi api, AssetRef baseRef, AssetRef quoteRef, decimal amount, bool sellingXmr,
        bool fixedRate, IReadOnlyDictionary<string, string> sponsors, CancellationToken ct)
    {
        if (!IsPrivacyAllowed((api as IPrivacyLevel)?.PrivacyLevel))
        {
            return null;
        }

        if (!sellingXmr && api is not IExchangeBuyPriceApi)
        {
            return null;
        }

        // In fixed mode, only query exchanges that actually honour a fixed-rate quote.
        if (fixedRate && !FixedCapableKeys.Contains(api.ExchangeKey))
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ExchangeTimeout);
        try
        {
            var (rb, rq) = await ResolveExchangeIdsAsync(api.ExchangeKey, baseRef, quoteRef, cts.Token);
            var query = new PriceQuery(rb, rq, amount, Fixed: fixedRate);
            var res = sellingXmr
                ? await api.GetSellPriceAsync(query, cts.Token)
                : await ((IExchangeBuyPriceApi)api).GetBuyPriceAsync(query, cts.Token);

            if (res is null || res.Price <= 0)
            {
                return null;
            }

            var receive = sellingXmr ? amount * res.Price : amount / res.Price;
            if (receive <= 0)
            {
                return null;
            }

            var nk = NormName(api.SiteName);
            var isSponsor = sponsors.TryGetValue(nk, out var sponsorLink) && !string.IsNullOrWhiteSpace(sponsorLink);
            var outHref = isSponsor ? sponsorLink : api.SiteUrl;
            var rateType = fixedRate ? RateTypes.Fixed : ((api as IRateType)?.RateType ?? RateTypes.Float);

            return new SwapQuoteRow(
                api.ExchangeKey,
                api.SiteName,
                receive,
                res.Price,
                (api as IPrivacyLevel)?.PrivacyLevel,
                rateType,
                isSponsor,
                outHref,
                "/exchange/" + Slugify(api.SiteName),
                res.MinAmountUsd ?? (api as IMinAmountUsd)?.MinAmountUsd);
        }
        catch
        {
            return null;
        }
    }

    // ── Sponsor lookup (normalized name → direct link), briefly cached (singleton service). ──
    private readonly SemaphoreSlim sponsorLock = new(1, 1);
    private Dictionary<string, string> sponsorCache = new(StringComparer.Ordinal);
    private DateTime sponsorCachedAt = DateTime.MinValue;

    private async Task<IReadOnlyDictionary<string, string>> GetSponsorsAsync(CancellationToken ct)
    {
        var ttl = TimeSpan.FromMinutes(this.config.GetValue("Sponsors:CacheTtlMinutes", 20));
        if (this.sponsorCachedAt != DateTime.MinValue && DateTime.UtcNow - this.sponsorCachedAt < ttl)
        {
            return this.sponsorCache;
        }

        await this.sponsorLock.WaitAsync(CancellationToken.None);
        try
        {
            if (this.sponsorCachedAt != DateTime.MinValue && DateTime.UtcNow - this.sponsorCachedAt < ttl)
            {
                return this.sponsorCache;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var url = this.config["Sponsors:SourceUrl"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                var client = this.httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var json = await client.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var link = el.TryGetProperty("link", out var l) ? l.GetString() : null;
                        var active = true;
                        if (el.TryGetProperty("expirationDate", out var e) && e.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal, out var exp))
                        {
                            active = exp > DateTime.UtcNow;
                        }

                        if (active && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(link))
                        {
                            map[NormName(name)] = link!;
                        }
                    }
                }
            }

            this.sponsorCache = map;
            this.sponsorCachedAt = DateTime.UtcNow;
            return map;
        }
        catch
        {
            this.sponsorCachedAt = DateTime.UtcNow;
            return this.sponsorCache;
        }
        finally
        {
            this.sponsorLock.Release();
        }
    }

    private static string NormName(string? s)
        => new string((s ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Slugify(string? s)
    {
        var lower = (s ?? string.Empty).ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastDash = false;
        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); lastDash = false; }
            else if (!lastDash) { sb.Append('-'); lastDash = true; }
        }

        return sb.ToString().Trim('-');
    }

    private async Task<(AssetRef Base, AssetRef Quote)> ResolveExchangeIdsAsync(
        string exchangeKey, AssetRef @base, AssetRef quote, CancellationToken ct)
    {
        var currencies = await GetCurrenciesAsync(exchangeKey, ct);
        if (currencies.Count == 0)
        {
            return (@base, quote);
        }

        var baseId = FindExchangeId(currencies, @base.Ticker, @base.Network);
        var quoteId = FindExchangeId(currencies, quote.Ticker, quote.Network);
        var rb = string.IsNullOrWhiteSpace(baseId) ? @base : @base with { ExchangeId = baseId };
        var rq = string.IsNullOrWhiteSpace(quoteId) ? quote : quote with { ExchangeId = quoteId };
        return (rb, rq);
    }

    private static string? FindExchangeId(IReadOnlyList<ExchangeCurrency> currencies, string ticker, string? network)
    {
        if (!string.IsNullOrWhiteSpace(network))
        {
            var net = currencies.FirstOrDefault(c =>
                c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                c.Network.Equals(network, StringComparison.OrdinalIgnoreCase))?.ExchangeId;
            if (!string.IsNullOrWhiteSpace(net))
            {
                return net;
            }
        }

        return currencies.FirstOrDefault(c =>
            c.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase))?.ExchangeId;
    }

    private async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(string exchangeKey, CancellationToken ct)
    {
        var api = this.currencyApis.FirstOrDefault(x =>
            x.ExchangeKey.Equals(exchangeKey, StringComparison.OrdinalIgnoreCase));
        if (api is null)
        {
            return System.Array.Empty<ExchangeCurrency>();
        }

        var key = $"swapcurrencies:{exchangeKey}";
        if (this.cache.TryGetValue(key, out IReadOnlyList<ExchangeCurrency>? cached) && cached is not null)
        {
            return cached;
        }

        var sem = this.locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(CancellationToken.None);
        try
        {
            if (this.cache.TryGetValue(key, out cached) && cached is not null)
            {
                return cached;
            }

            IReadOnlyList<ExchangeCurrency> list;
            try
            {
                list = await api.GetCurrenciesAsync(ct);
            }
            catch
            {
                list = System.Array.Empty<ExchangeCurrency>();
            }

            this.cache.Set(key, list, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CurrenciesTtl });
            return list;
        }
        finally
        {
            sem.Release();
        }
    }
}
