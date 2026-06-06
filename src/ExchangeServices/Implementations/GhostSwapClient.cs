using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// GhostSwap partner API client.
///
/// Docs:     https://partners.ghostswap.io/docs
/// Base URL: https://partners-api.ghostswap.io
///
/// Auth: Authorization: Bearer {publicKey}:{secret}
///   - The colon-joined credential goes AFTER "Bearer ". No base64.
///   - publicKey looks like gspk_live_..., secret looks like gssk_live_...
///
/// Endpoints used:
///   GET  /v1/currencies?lite=true  — bare ticker list (e.g. ["xmr","btc","usdtrx",...])
///   GET  /v1/currencies            — full ticker+network objects
///   POST /v1/quotes                — body: { from, to, amountFrom }
///                                  → { quote: { amountUserReceives, ... } }
///
/// Ticker conventions observed from /v1/currencies?lite=true:
///   xmr           — bare ticker (no network suffix)
///   usdtrx        — USDT on Tron        (canonical; aliases usdttrx/usdttrc20/usdttron also accepted)
///   usdt20        — USDT on Ethereum    (short for ERC-20)
///   usdtbsc       — USDT on BSC
///   usdtpolygon   — USDT on Polygon
///   usdtsol       — USDT on Solana
///   usdtarb       — USDT on Arbitrum
///   usdtop        — USDT on Optimism
///   usdtavac      — USDT on Avalanche C-Chain
///   usdtxtz       — USDT on Tezos
///   usdtnear      — USDT on NEAR
///   (USDC, BTC, etc. follow the same suffix pattern)
///
/// Note: USDT on Tron's canonical "usdtrx" collapses the leading "t" of TRX
/// into the "t" of USDT, so it doesn't decompose cleanly as {ticker}{suffix}.
/// Both encode (ResolveCode) and decode (DecodeLiteCode) special-case it.
///
/// Rate semantics:
///   Sell (XMR → USDT): amountFrom=1 (XMR) → amountUserReceives = USDT per 1 XMR (direct)
///   Buy  (USDT → XMR): amountFrom=BuyRef (USDT) → amountUserReceives = XMR received
///                       buyPrice = BuyRef / amountUserReceives
/// </summary>
public sealed class GhostSwapClient : IGhostSwapClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient http;
    private readonly GhostSwapOptions opt;

    public string  ExchangeKey  => "ghostswap";
    public string  SiteName     => opt.SiteName;
    public string? SiteUrl      => opt.SiteUrl;
    public char    PrivacyLevel => opt.PrivacyLevel;

    // MinAmountUsd: returns the live API minimum when available, otherwise falls back to config.
    // Populated in GetBuyPriceAsync where the from-side is USDT, so minAmountFrom is already in USD terms.
    private decimal? _apiMinAmountUsd;
    private readonly object _apiMinAmountLock = new();
    public decimal MinAmountUsd => _apiMinAmountUsd ?? opt.MinAmountUsd;

    // Currencies cache
    private readonly object _currenciesLock = new();
    private DateTimeOffset _currenciesAtUtc;
    private List<ExchangeCurrency>? _cachedCurrencies;

    public GhostSwapClient(HttpClient http, IOptions<GhostSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR → USDT
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!IsConfigured()) return null;

        var from = ResolveCode(query.Base);
        var to = ResolveCode(query.Quote);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;

        foreach (var (f, t) in ExpandPairCandidates(from, to))
        {
            ct.ThrowIfCancellationRequested();

            var dto = await PostQuoteAsync(f, t, 1m, ct);
            if (dto?.Quote is null) continue;

            // Prefer amountTo (gross destination) over amountUserReceives (net after
            // partner margin + network/withdrawal fee). The public-facing GhostSwap
            // site shows the gross rate, so using amountTo keeps our aggregator
            // comparable. amountUserReceives is what a customer would actually
            // receive on-chain, which depends on the destination network's fee and
            // isn't really a "market price" for cross-exchange comparison.
            var grossOut = dto.Quote.AmountTo > 0
                ? dto.Quote.AmountTo
                : dto.Quote.AmountUserReceives;
            if (grossOut <= 0) continue;

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: grossOut, // USDT per 1 XMR (gross, pre-fee)
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: dto.Quote.QuoteId,
                Raw: null
            );
        }

        return null;
    }

    // =========================
    // BUY: ? USDT → 1 XMR
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!IsConfigured()) return null;

        var from = ResolveCode(query.Quote); // pay USDT
        var to = ResolveCode(query.Base);    // receive XMR
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;

        var probe = opt.BuyReferenceAmountUsdt > 0 ? opt.BuyReferenceAmountUsdt : 100m;

        foreach (var (f, t) in ExpandPairCandidates(from, to))
        {
            ct.ThrowIfCancellationRequested();

            var dto = await PostQuoteAsync(f, t, probe, ct);
            if (dto?.Quote is null) continue;

            // Same rationale as the sell side: amountTo is the gross XMR destination
            // before partner/network deductions; amountUserReceives is post-fee.
            // Using amountTo gives us a buy price comparable to what GhostSwap shows
            // on its public site.
            var xmrOut = dto.Quote.AmountTo > 0
                ? dto.Quote.AmountTo
                : dto.Quote.AmountUserReceives;
            if (xmrOut <= 0) continue;

            var usdtPerXmr = probe / xmrOut;
            if (usdtPerXmr <= 0) continue;

            // amountFrom side is USDT here, so any min surfaced by the API is approx USD.
            if (dto.Quote.MinAmountFrom > 0)
            {
                lock (_apiMinAmountLock)
                    _apiMinAmountUsd = dto.Quote.MinAmountFrom;
            }

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: usdtPerXmr,
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: dto.Quote.QuoteId,
                Raw: null
            );
        }

        return null;
    }

    // =========================
    // CURRENCIES: GET /v1/currencies
    // Try the full, network-aware response first; fall back to ?lite=true (bare composite codes).
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        // cache
        lock (_currenciesLock)
        {
            if (_cachedCurrencies is not null &&
                (DateTimeOffset.UtcNow - _currenciesAtUtc).TotalSeconds < Math.Max(10, opt.CurrenciesCacheSeconds))
            {
                return _cachedCurrencies;
            }
        }

        if (!IsConfigured()) return Array.Empty<ExchangeCurrency>();

        var list = await GetCurrenciesFullAsync(ct);

        if (list.Count == 0)
            list = await GetCurrenciesLiteAsync(ct);

        if (list.Count > 0)
        {
            lock (_currenciesLock)
            {
                _cachedCurrencies = list;
                _currenciesAtUtc = DateTimeOffset.UtcNow;
            }
        }

        return list;
    }

    private async Task<List<ExchangeCurrency>> GetCurrenciesFullAsync(CancellationToken ct)
    {
        var res = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/currencies");
            AddAuth(req);
            return req;
        }, ct);

        if (res is null) return new List<ExchangeCurrency>();
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices)
            return new List<ExchangeCurrency>();

        return ParseCurrenciesFull(res.Body);
    }

    private async Task<List<ExchangeCurrency>> GetCurrenciesLiteAsync(CancellationToken ct)
    {
        var res = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/v1/currencies?lite=true");
            AddAuth(req);
            return req;
        }, ct);

        if (res is null) return new List<ExchangeCurrency>();
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices)
            return new List<ExchangeCurrency>();

        return ParseCurrenciesLite(res.Body);
    }

    private static List<ExchangeCurrency> ParseCurrenciesFull(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("currencies", out var cEl) &&
                cEl.ValueKind == JsonValueKind.Array)
                arr = cEl;
            else if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else
                return new List<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>();

            foreach (var el in arr.EnumerateArray())
            {
                // Lite-form fallback if a string slipped through
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    var (t, n) = DecodeLiteCode(s);
                    list.Add(new ExchangeCurrency(
                        ExchangeId: s.Trim().ToLowerInvariant(),
                        Ticker: t,
                        Network: n
                    ));
                    continue;
                }

                if (el.ValueKind != JsonValueKind.Object) continue;

                var ticker =
                    GetString(el, "ticker") ??
                    GetString(el, "symbol") ??
                    GetString(el, "code") ??
                    GetString(el, "currency") ??
                    "";

                if (string.IsNullOrWhiteSpace(ticker)) continue;

                var network =
                    GetString(el, "network") ??
                    GetString(el, "networkCode") ??
                    GetString(el, "chain") ??
                    "";

                var name = GetString(el, "name");

                var id =
                    GetString(el, "id") ??
                    GetString(el, "currencyId") ??
                    (string.IsNullOrWhiteSpace(network)
                        ? ticker.Trim().ToLowerInvariant()
                        : $"{ticker.Trim().ToLowerInvariant()}{network.Trim().ToLowerInvariant()}");

                list.Add(new ExchangeCurrency(
                    ExchangeId: id.Trim().ToLowerInvariant(),
                    Ticker: ticker.Trim().ToUpperInvariant(),
                    Network: PrettyNetwork(network, name)
                ));
            }

            return list
                .GroupBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Network, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<ExchangeCurrency>();
        }

        static string? GetString(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
    }

    /// <summary>
    /// Parses /v1/currencies?lite=true output — a bare array (or { currencies: [...] })
    /// of composite codes like "usdtrx", "usdt20", "xmr". Decomposes each into a
    /// proper (Ticker, Network) record so the master pair list joins on canonical
    /// USDT/USDC/etc. entries instead of seeing phantom "USDTRX" tickers.
    /// </summary>
    private static List<ExchangeCurrency> ParseCurrenciesLite(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("currencies", out var cEl) &&
                cEl.ValueKind == JsonValueKind.Array)
                arr = cEl;
            else if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else
                return new List<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>();

            foreach (var el in arr.EnumerateArray())
            {
                var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                if (string.IsNullOrWhiteSpace(s)) continue;

                var (ticker, network) = DecodeLiteCode(s);

                list.Add(new ExchangeCurrency(
                    ExchangeId: s.Trim().ToLowerInvariant(),
                    Ticker: ticker,
                    Network: network
                ));
            }

            return list
                .GroupBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Network, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<ExchangeCurrency>();
        }
    }

    /// <summary>
    /// Decomposes a GhostSwap composite code (e.g. "usdtrx", "usdt20", "xmr")
    /// into a (Ticker, Network) pair. Bare codes return (UPPER, "").
    /// </summary>
    private static (string Ticker, string Network) DecodeLiteCode(string code)
    {
        var c = code.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(c)) return ("", "");

        // Canonical aliases that break the {ticker}{suffix} pattern.
        // GhostSwap's catalog returns "usdtrx" (not "usdttrx") for USDT on Tron — the
        // leading "t" of TRX collapses into the trailing "t" of USDT. Special-cased so
        // the parser doesn't strip "usdt" and leave a meaningless "rx" suffix.
        // (The API still accepts usdttrx/usdttrc20/usdttron as aliases, but canonical wins.)
        switch (c)
        {
            case "usdtrx": return ("USDT", "Tron");
        }

        // Multi-network stablecoins use a {ticker}{suffix} pattern.
        foreach (var stable in new[] { "usdt", "usdc" })
        {
            if (c.Length > stable.Length && c.StartsWith(stable))
            {
                var suffix = c[stable.Length..];
                var network = SuffixToNetwork(suffix);
                if (!string.IsNullOrWhiteSpace(network))
                    return (stable.ToUpperInvariant(), network);
            }
        }

        // Bare ticker (xmr, btc, eth, ltc, etc.)
        return (c.ToUpperInvariant(), "");
    }

    private static string SuffixToNetwork(string suffix) => suffix switch
    {
        "trx"     => "Tron",                 // legacy alias suffix; canonical USDT/Tron is "usdtrx"
        "20"      => "Ethereum",             // ERC-20 abbreviation
        "bsc"     => "Binance Smart Chain",
        "polygon" => "Polygon",
        "sol"     => "Solana",
        "arb"     => "Arbitrum",
        "op"      => "Optimism",
        "avac"    => "Avalanche C-Chain",
        "xtz"     => "Tezos",
        "near"    => "NEAR",
        "monad"   => "Monad",
        "plasma"  => "Plasma",
        "bera"    => "Berachain",
        "hype"    => "Hyperliquid",
        _         => ""                       // unknown — caller treats as a non-stable bare ticker
    };

    // =========================
    // QUOTE CALL
    // POST /v1/quotes   body: { from, to, amountFrom }
    // =========================
    private async Task<QuoteResponse?> PostQuoteAsync(
        string from, string to, decimal amountFrom, CancellationToken ct)
    {
        var body = new QuoteRequest
        {
            From = from,
            To = to,
            AmountFrom = amountFrom.ToString("0.########", CultureInfo.InvariantCulture)
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);

        var res = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/v1/quotes");
            AddAuth(req);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }, ct);

        if (res is null) return null;
        
        // 502 with upstream_empty_quote is a permanent "no quote available" — don't waste
        // retries or trigger cancellation cascades. Treat it the same as any other terminal
        // 4xx/5xx by returning null immediately.
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices)
            return null;
        
        try { return JsonSerializer.Deserialize<QuoteResponse>(res.Body, JsonOpts); }
        catch { return null; }
    }

    // =========================
    // HTTP
    // =========================
    private Task<SafeHttp.Result?> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        return http.SendForStringWithRetryAsync(requestFactory, timeout, Math.Clamp(opt.RetryCount, 0, 6), ct);
    }

    private void AddAuth(HttpRequestMessage req)
    {
        if (req.Headers.Accept.Count == 0)
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Railway's edge does not respond to Expect: 100-continue. Without this, .NET sends
        // the POST headers, blocks waiting for a "100 Continue" that never comes, and the
        // request times out before the body ever leaves our socket. Curl works over H2 where
        // this mechanism doesn't exist at all.
        req.Headers.ExpectContinue = false;

        // GhostSwap auth: Authorization: Bearer {publicKey}:{secret}
        // TryAddWithoutValidation so the colon isn't reinterpreted by the header parser.
        req.Headers.TryAddWithoutValidation(
            "Authorization",
            $"Bearer {opt.PublicKey}:{opt.Secret}");

        if (req.Headers.UserAgent.Count == 0 && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    private bool IsConfigured()
        => !string.IsNullOrWhiteSpace(opt.PublicKey) && !string.IsNullOrWhiteSpace(opt.Secret);

    // =========================
    // CODE RESOLUTION (AssetRef → GhostSwap composite code)
    //
    // PriceService delivers canonical AssetRefs with Network populated
    // (e.g. Ticker="USDT", Network="Tron"). We fold Ticker+Network straight
    // into GhostSwap's composite — no API round-trips needed in the common case.
    // ExpandPairCandidates remains as a fallback when Network is missing.
    // =========================
    private static string ResolveCode(AssetRef asset)
    {
        // Explicit ExchangeId wins — these are already in GhostSwap's native form
        // (e.g. when seeded from our own GetCurrenciesAsync output via the lite parser).
        var ex = (asset.ExchangeId ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ex)) return ex;

        var ticker = (asset.Ticker ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ticker)) return "";

        // Multi-network stablecoins: combine ticker + network suffix.
        if (ticker is "usdt" or "usdc")
        {
            // USDT on Tron's canonical is "usdtrx" — doesn't fit the {ticker}{suffix}
            // pattern (the API also accepts usdttrx/usdttrc20/usdttron as aliases, but
            // their docs and catalog use the canonical, so prefer it).
            if (ticker == "usdt")
            {
                var n = (asset.Network ?? "").Trim().ToLowerInvariant();
                if (n is "tron" or "trc20" or "trx")
                    return "usdtrx";
            }

            var suffix = NetworkToSuffix(asset.Network);
            if (!string.IsNullOrEmpty(suffix))
                return ticker + suffix;
            // No network hint — leave as bare; ExpandStable will probe candidates.
        }

        return ticker;
    }

    private static string NetworkToSuffix(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return "";

        return network.Trim().ToLowerInvariant() switch
        {
            // Tron intentionally omitted here — USDT/Tron is canonicalised to "usdtrx"
            // in ResolveCode before this method is reached. If USDC/Tron is ever needed,
            // we'd map it here (likely "trx" since email only confirmed USDT's quirk).
            "ethereum" or "erc20" or "eth"         => "20",
            "binance smart chain" or "bsc" or
            "bep20" or "binance"                   => "bsc",
            "polygon" or "matic"                   => "polygon",
            "solana" or "sol"                      => "sol",
            "arbitrum" or "arb" or "arbitrum one"  => "arb",
            "optimism" or "op"                     => "op",
            "avalanche c-chain" or "avalanche" or
            "avaxc" or "avax"                      => "avac",
            "tezos" or "xtz"                       => "xtz",
            "near"                                 => "near",
            "monad"                                => "monad",
            "plasma"                               => "plasma",
            "berachain" or "bera"                  => "bera",
            "hyperliquid" or "hype"                => "hype",
            _                                      => ""
        };
    }

    private static IEnumerable<(string From, string To)> ExpandPairCandidates(string from, string to)
    {
        // First attempt: pass through as-is — ResolveCode usually produces the right
        // composite already (e.g. "usdtrx") so this is almost always the only call.
        yield return (from, to);

        // Fallback: if either side is a bare "usdt"/"usdc" (no network hint reached
        // us), probe common network variants in priority order.
        var fAlts = ExpandStable(from).ToList();
        var tAlts = ExpandStable(to).ToList();
        if (fAlts.Count == 1 && tAlts.Count == 1) yield break;

        foreach (var f in fAlts)
            foreach (var t in tAlts)
            {
                if (f == from && t == to) continue;
                yield return (f, t);
            }
    }

    private static IEnumerable<string> ExpandStable(string code)
    {
        yield return code;

        if (code == "usdt")
        {
            yield return "usdtrx";      // TRC-20 — canonical XMR↔USDT route
            yield return "usdt20";      // ERC-20
            yield return "usdtbsc";
            yield return "usdtpolygon";
            yield return "usdtsol";
        }
        else if (code == "usdc")
        {
            yield return "usdc20";      // ERC-20 is the dominant USDC
            yield return "usdctrx";
            yield return "usdcbsc";
            yield return "usdcsol";
        }
    }

    private static string PrettyNetwork(string network, string? coinName)
    {
        if (string.IsNullOrWhiteSpace(network))
            return string.IsNullOrWhiteSpace(coinName) ? "" : "Mainnet";

        return network.Trim().ToLowerInvariant() switch
        {
            "btc" or "bitcoin"             => "Bitcoin",
            "eth" or "erc20" or "ethereum" => "Ethereum",
            "trx" or "trc20" or "tron"     => "Tron",
            "bsc" or "bep20" or "binance"  => "Binance Smart Chain",
            "sol" or "solana"              => "Solana",
            "matic" or "polygon"           => "Polygon",
            "arb" or "arbitrum"            => "Arbitrum",
            "op" or "optimism"             => "Optimism",
            "avac" or "avalanche"          => "Avalanche C-Chain",
            "base"                         => "Base",
            "ltc" or "litecoin"            => "Litecoin",
            "xmr" or "monero" or "mainnet" => "Mainnet",
            _ => network.Trim()
        };
    }

    // =========================
    // DTOs
    // =========================
    private sealed class QuoteRequest
    {
        [JsonPropertyName("from")]       public string From { get; set; } = "";
        [JsonPropertyName("to")]         public string To { get; set; } = "";
        [JsonPropertyName("amountFrom")] public string AmountFrom { get; set; } = "0";
    }

    private sealed class QuoteResponse
    {
        [JsonPropertyName("quote")] public QuoteData? Quote { get; set; }
    }

    private sealed class QuoteData
    {
        [JsonPropertyName("amountUserReceives")] public decimal AmountUserReceives { get; set; }
        [JsonPropertyName("amountFrom")]         public decimal AmountFrom { get; set; }
        [JsonPropertyName("amountTo")]           public decimal AmountTo { get; set; }
        [JsonPropertyName("rate")]               public decimal Rate { get; set; }
        [JsonPropertyName("minAmountFrom")]      public decimal MinAmountFrom { get; set; }
        [JsonPropertyName("maxAmountFrom")]      public decimal MaxAmountFrom { get; set; }
        [JsonPropertyName("quoteId")]            public string? QuoteId { get; set; }
        [JsonPropertyName("from")]               public string? From { get; set; }
        [JsonPropertyName("to")]                 public string? To { get; set; }
    }
}