using System.Collections.Concurrent;
using System.Globalization;
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
    private static readonly TimeSpan CurrenciesFailureTtl = TimeSpan.FromMinutes(2);

    // Each uncached request fans out to every exchange (~40 outbound calls). Anyone can hit /swap,
    // so: only the assets the page offers, identical requests share a short-lived result, and at
    // most a few fan-outs run at once. Without this a simple loop could make the server hammer the
    // exchanges until they rate-limit or ban its IP — the same IP the price board polls from.
    private static readonly HashSet<string> SwapAssets = new(StringComparer.OrdinalIgnoreCase) { "btc", "eth", "usdt" };
    private static readonly TimeSpan QuoteCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly SemaphoreSlim FanOutGate = new(3, 3);
    private static readonly TimeSpan FanOutWait = TimeSpan.FromSeconds(15);

    // The concurrency gate above limits how many fan-outs run at once, not how many start, so a
    // rolling per-minute budget caps the total (configurable as Swap:FanOutsPerMinute).
    public const int DefaultFanOutsPerMinute = 20;
    private readonly int fanOutsPerMinute;
    private readonly Queue<DateTime> fanOutStarts = new();
    private readonly object startLock = new();
    private readonly Dictionary<string, SharedFanOut> inflight = new(StringComparer.Ordinal);

    /// <summary>Time source for the budget (replaceable in tests).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    // Rough per-asset amount range, about $10 to $1 million at recent prices. Amounts outside it
    // are quoted at the nearest edge (the rate is still applied to the exact amount asked).
    private static readonly Dictionary<string, (decimal Min, decimal Max)> AmountRange = new(StringComparer.Ordinal)
    {
        ["xmr"] = (0.02m, 5_000m),
        ["btc"] = (0.0001m, 20m),
        ["eth"] = (0.003m, 400m),
        ["usdt"] = (10m, 1_000_000m),
    };

    // Steps inside each power of ten. About ten per decade keeps the snapped amount within roughly
    // 12% of what was typed, so the per-unit rate stays representative.
    private static readonly decimal[] Steps = { 1m, 1.25m, 1.5m, 2m, 2.5m, 3m, 4m, 5m, 6m, 8m, 10m };

    // Exchanges whose client returns a genuine fixed-rate quote when PriceQuery.Fixed is set. In
    // fixed mode we only query these (others would just echo their float rate).
    private static readonly HashSet<string> FixedCapableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "changenow", "fixedfloat", "exolix", "stealthex", "simpleswap", "trocador", "letsexchange",
        "0trace", "swapuz", "changee", "swapgate", "bitania", "quickex", "pegasusswap", "sageswap",
        "swapzone", "elcapo", "explace", "ccecash", "etzswap", "alfacash", "flashift", "anonchange", "mistex", "betkarma",
    };

    public SwapQuoteService(
        IEnumerable<IExchangePriceApi> priceApis,
        IEnumerable<IExchangeCurrencyApi> currencyApis,
        IMemoryCache cache,
        IHttpClientFactory httpFactory,
        IConfiguration config)
    {
        // Exchanges removed via PriceService:ExcludedExchanges (scams, broken partners) must not
        // show up in live swap quotes either, not just on the price board.
        var excluded = new HashSet<string>(
            config.GetSection("PriceService:ExcludedExchanges").Get<string[]>() ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        this.priceApis = priceApis.Where(a => !excluded.Contains(a.ExchangeKey)).ToList();
        this.currencyApis = currencyApis.ToList();
        this.cache = cache;
        this.httpFactory = httpFactory;
        this.config = config;
        this.fanOutsPerMinute = Math.Max(1, config.GetValue("Swap:FanOutsPerMinute", DefaultFanOutsPerMinute));
    }

    /// <summary>
    /// The amount exchanges are actually asked about: clamped into the asset's sensible range,
    /// then snapped to the nearest step (1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8 × a power of ten).
    /// </summary>
    public static decimal SnapAmount(string fromTicker, decimal amount)
    {
        var (min, max) = AmountRange.TryGetValue((fromTicker ?? string.Empty).ToLowerInvariant(), out var r)
            ? r
            : (0.0001m, 1_000_000m);
        var clamped = Math.Clamp(amount, min, max);

        var magnitude = (int)Math.Floor(Math.Log10((double)clamped));
        var scale = ExchangeServices.DecimalConvert.FromDouble(Math.Pow(10, magnitude));
        var mantissa = clamped / scale;

        // Nearest step in ratio terms (log distance), so 1.1 snaps to 1 and 7.1 snaps to 8.
        var best = Steps[0];
        var bestDist = double.MaxValue;
        foreach (var step in Steps)
        {
            var dist = Math.Abs(Math.Log((double)(mantissa / step)));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = step;
            }
        }

        var snapped = best * scale;
        // Snapping must not step back outside the range.
        return Math.Clamp(snapped, min, max);
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
    /// Starts (or joins) a quote run for <paramref name="fromTicker"/> → <paramref name="toTicker"/>
    /// (one of which must be XMR). Returns null for an invalid request. The returned session reads
    /// each exchange's quote, fastest-first, already enriched with sponsor status + links, with the
    /// amount received worked out for the exact amount asked. When the per-minute budget of
    /// fresh fan-outs is used up the session is marked <see cref="SwapQuoteSession.Busy"/> and
    /// returns no rows, so the page can say "busy, try again in a minute".
    /// </summary>
    public SwapQuoteSession? Start(string fromTicker, string toTicker, decimal amount, bool fixedRate = false)
    {
        fromTicker = (fromTicker ?? string.Empty).Trim().ToLowerInvariant();
        toTicker = (toTicker ?? string.Empty).Trim().ToLowerInvariant();
        if (amount <= 0 || fromTicker.Length == 0 || toTicker.Length == 0 || fromTicker == toTicker)
        {
            return null;
        }

        var sellingXmr = fromTicker == "xmr";
        var asset = sellingXmr ? toTicker : fromTicker;
        if (asset == "xmr" || (!sellingXmr && toTicker != "xmr"))
        {
            return null; // exactly one side must be XMR
        }

        if (!SwapAssets.Contains(asset))
        {
            return null; // only the assets the swap page offers
        }

        // Exchanges are asked about a nearby "step" amount instead of the exact one, and the step
        // is kept inside a sensible range. That keeps the number of different requests (and so
        // the number of fan-outs anyone can force) small; each exchange's per-unit rate is then
        // applied to the exact amount the visitor typed.
        var quoteAmount = SnapAmount(fromTicker, amount);
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"swapq:{fromTicker}:{toTicker}:{quoteAmount}:{fixedRate}");

        SharedFanOut run;
        lock (this.startLock)
        {
            if (this.cache.TryGetValue(cacheKey, out SharedFanOut? cached) && cached is not null)
            {
                run = cached;
            }
            else if (this.inflight.TryGetValue(cacheKey, out var running))
            {
                run = running; // someone already asked for this; share their run
            }
            else
            {
                if (!TakeFanOutSlot())
                {
                    return new SwapQuoteSession(null, amount, sellingXmr, busy: true);
                }

                run = new SharedFanOut();
                this.inflight[cacheKey] = run;

                // Runs to the end on its own, with no link to the visitor's connection, so a client
                // that disconnects early still leaves a cached result instead of a wasted fan-out.
                _ = Task.Run(() => RunFanOutAsync(run, cacheKey, fromTicker, asset, quoteAmount, sellingXmr, fixedRate));
            }
        }

        return new SwapQuoteSession(run, amount, sellingXmr, busy: false);
    }

    /// <summary>Convenience wrapper over <see cref="Start"/> for callers that only need the rows.</summary>
    public async IAsyncEnumerable<SwapQuoteRow> GetQuotesStream(
        string fromTicker, string toTicker, decimal amount, bool fixedRate = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = Start(fromTicker, toTicker, amount, fixedRate);
        if (session is null)
        {
            yield break;
        }

        await foreach (var row in session.ReadAsync(ct))
        {
            yield return row;
        }
    }

    // Rolling budget of fresh (uncached, not shared) fan-outs per minute, site-wide.
    private bool TakeFanOutSlot()
    {
        var now = this.Clock();
        while (this.fanOutStarts.Count > 0 && now - this.fanOutStarts.Peek() >= TimeSpan.FromMinutes(1))
        {
            this.fanOutStarts.Dequeue();
        }

        if (this.fanOutStarts.Count >= this.fanOutsPerMinute)
        {
            return false;
        }

        this.fanOutStarts.Enqueue(now);
        return true;
    }

    private async Task RunFanOutAsync(
        SharedFanOut run, string cacheKey, string fromTicker, string asset, decimal amount, bool sellingXmr, bool fixedRate)
    {
        var gateTaken = false;
        var completed = false;
        try
        {
            gateTaken = await FanOutGate.WaitAsync(FanOutWait);
            if (!gateTaken)
            {
                run.MarkBusy(); // too many live quote runs at once; the page says "busy"
                return;
            }

            // Parse the pair the SAME way the board does so each client receives the ticker + network
            // it needs (e.g. "USDTTRC" → ticker USDT on Tron) — otherwise ticker-mapping clients (like
            // xmr2cex) can't resolve the asset and silently return nothing.
            var baseRef = CryptoPriceNow.Services.PriceService.ParseAssetPublic("XMR");
            var quoteRef = CryptoPriceNow.Services.PriceService.ParseAssetPublic(
                asset == "usdt" ? "USDTTRC" : asset.ToUpperInvariant());

            var sponsors = await GetSponsorsAsync(CancellationToken.None);

            var tasks = this.priceApis
                .Select(api => FetchOneAsync(api, baseRef, quoteRef, amount, sellingXmr, fixedRate, sponsors, CancellationToken.None))
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
                    // one exchange failing must not stop the run
                }

                if (row is not null && row.Receive > 0)
                {
                    run.Add(row);
                }
            }

            completed = true;
        }
        catch
        {
            // Unexpected failure: finish the run with whatever arrived, but do not cache it.
        }
        finally
        {
            if (gateTaken)
            {
                FanOutGate.Release();
            }

            lock (this.startLock)
            {
                if (completed)
                {
                    this.cache.Set(cacheKey, run, QuoteCacheTtl);
                }

                this.inflight.Remove(cacheKey);
            }

            run.Complete();
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
            var outHref = isSponsor ? sponsorLink : (IsWebLink(api.SiteUrl) ? api.SiteUrl : null);
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

                        // Only plain web links: the feed is outside this site's control, and a
                        // javascript: or data: link would run script when a visitor clicks "Swap".
                        if (active && !string.IsNullOrWhiteSpace(name) && IsWebLink(link))
                        {
                            map[NormName(name)] = link!.Trim();
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

    internal static bool IsWebLink(string? link)
        => !string.IsNullOrWhiteSpace(link)
           && Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

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

    internal async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(string exchangeKey, CancellationToken ct)
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
        await sem.WaitAsync(ct);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the caller gave up — don't cache anything for it
            }
            catch
            {
                // Transient failure (incl. the client's own timeout): NOT cached, so the next
                // request retries instead of the exchange being blanked for the full hour.
                return System.Array.Empty<ExchangeCurrency>();
            }

            // A successful-but-empty list usually means the exchange's currency endpoint failed
            // softly (clients return [] on error) — keep it only briefly.
            var ttl = list.Count > 0 ? CurrenciesTtl : CurrenciesFailureTtl;
            this.cache.Set(key, list, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
            return list;
        }
        finally
        {
            sem.Release();
        }
    }
}

/// <summary>
/// One fan-out to every exchange, shared by everyone who asks for the same quote while it runs
/// and kept briefly in the cache once finished. Rows are appended as exchanges answer; readers
/// wait for new rows without holding up the run.
/// </summary>
internal sealed class SharedFanOut
{
    private readonly object gate = new();
    private readonly List<SwapQuoteRow> rows = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool done;

    /// <summary>True when the run could not start because too many were already running.</summary>
    public bool Busy { get; private set; }

    public void Add(SwapQuoteRow row)
    {
        TaskCompletionSource signal;
        lock (this.gate)
        {
            this.rows.Add(row);
            signal = this.changed;
            this.changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
    }

    public void MarkBusy()
    {
        lock (this.gate)
        {
            this.Busy = true;
        }
    }

    public void Complete()
    {
        TaskCompletionSource signal;
        lock (this.gate)
        {
            this.done = true;
            signal = this.changed;
        }

        signal.TrySetResult();
    }

    /// <summary>Rows from <paramref name="from"/> on, whether the run has finished, and a task that
    /// completes when more arrive.</summary>
    public (List<SwapQuoteRow> Rows, bool Done, Task Next) Read(int from)
    {
        lock (this.gate)
        {
            var slice = from < this.rows.Count ? this.rows.GetRange(from, this.rows.Count - from) : new List<SwapQuoteRow>();
            return (slice, this.done, this.changed.Task);
        }
    }
}

/// <summary>A visitor's view of a (possibly shared) quote run, for one exact amount.</summary>
public sealed class SwapQuoteSession
{
    private readonly SharedFanOut? run;
    private readonly decimal amount;
    private readonly bool sellingXmr;
    private readonly bool startBusy;

    internal SwapQuoteSession(SharedFanOut? run, decimal amount, bool sellingXmr, bool busy)
    {
        this.run = run;
        this.amount = amount;
        this.sellingXmr = sellingXmr;
        this.startBusy = busy;
    }

    /// <summary>True when the server is too busy to fetch fresh quotes; try again in a minute.
    /// Final once <see cref="ReadAsync"/> has finished.</summary>
    public bool Busy => this.startBusy || (this.run?.Busy ?? false);

    /// <summary>Each exchange's row as it arrives, with the amount received for the exact amount
    /// asked (the shared run quoted a nearby step amount; its per-unit rate is applied here).
    /// Stopping early (a disconnect) only stops this reader, never the shared run.</summary>
    public async IAsyncEnumerable<SwapQuoteRow> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (this.run is null)
        {
            yield break;
        }

        var next = 0;
        while (true)
        {
            var (rows, done, more) = this.run.Read(next);
            next += rows.Count;
            foreach (var row in rows)
            {
                if (row.PerXmr <= 0)
                {
                    continue;
                }

                var receive = this.sellingXmr ? this.amount * row.PerXmr : this.amount / row.PerXmr;
                yield return row with { Receive = receive };
            }

            if (done)
            {
                yield break;
            }

            await more.WaitAsync(ct);
        }
    }
}
