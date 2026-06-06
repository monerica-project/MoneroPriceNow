using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
/// 0trace Partner API client.
///
/// Docs:     https://0trace.io/api/quickstart
/// Base URL: https://0trace.io/api
///
/// Class is named ZeroTraceClient because C# identifiers can't begin with a
/// digit; ExchangeKey stays "0trace" so the brand is preserved in logs/caching.
///
/// Auth: HMAC-SHA256 of the EXACT request-body bytes, keyed with ApiSecret,
/// hex-encoded into X-API-SIGN. Plus X-API-KEY and X-API-NONCE (random hex).
///   - /v1/currencies and /v1/price both accept ANONYMOUS calls — no headers
///     at all. When ApiKey + ApiSecret aren't configured we omit the auth
///     headers entirely and still get base rates. This is what we use by
///     default for an aggregator UI.
///   - When both are configured we sign every call and receive partner-
///     specific quotes (afftax markup applies).
///   - The literal two bytes "{}" must be signed for empty bodies.
///
/// Endpoints used:
///   POST /v1/currencies   body: {}
///                         → { code: 0, data: [{ code, coin, network, send, ... }] }
///   POST /v1/price        body: { type, fromCcy, toCcy, direction, amount }
///                         → { code: 0, data: { from: {...}, to: {...}, markupBps } }
///
/// Ticker convention (from /v1/currencies):
///   xmr          — bare ticker for chain-native coins
///   usdt_trc20   — USDT on Tron       (underscore + network short)
///   usdt_eth     — USDT on Ethereum
///   usdc_arb     — USDC on Arbitrum
///   eth_bsc      — ETH on BSC (ETH is multi-network at 0trace)
///   dai_eth, wbtc_eth, btcb_bsc — other multi-network tokens
///   ...
///
/// Rate semantics:
///   Sell (XMR → USDT): direction="from", amount="1"
///                      data.to.amount is USDT received per 1 XMR (direct).
///   Buy  (USDT → XMR): direction="to",   amount="1"
///                      data.from.amount is USDT needed to receive 1 XMR (direct).
/// </summary>
public sealed class ZeroTraceClient : IZeroTraceClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // Important for signing: stable, no extra whitespace, no escaping changes.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient http;
    private readonly ZeroTraceOptions opt;

    public string  ExchangeKey  => "0trace";
    public string  SiteName     => opt.SiteName;
    public string? SiteUrl      => opt.SiteUrl;
    public char    PrivacyLevel => opt.PrivacyLevel;

    // MinAmountUsd: returns the live API minimum when available, otherwise falls back to config.
    private decimal? _apiMinAmountUsd;
    private readonly object _apiMinAmountLock = new();
    public decimal MinAmountUsd => _apiMinAmountUsd ?? opt.MinAmountUsd;

    // Currencies cache
    private readonly object _currenciesLock = new();
    private DateTimeOffset _currenciesAtUtc;
    private List<ExchangeCurrency>? _cachedCurrencies;

    public ZeroTraceClient(HttpClient http, IOptions<ZeroTraceOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR → USDT
    // direction="from", amount="1" — data.to.amount is the USDT we'd receive.
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var fromCcy = ResolveCode(query.Base);
        var toCcy = ResolveCode(query.Quote);
        if (string.IsNullOrWhiteSpace(fromCcy) || string.IsNullOrWhiteSpace(toCcy)) return null;

        var dto = await PostPriceAsync(
            type: "float",
            fromCcy: fromCcy,
            toCcy: toCcy,
            direction: "from",
            amount: 1m,
            ct: ct);

        if (dto?.Data?.To is null) return null;
        if (dto.Code != 0) return null;
        if (dto.Data.To.Amount <= 0) return null;

        // to.min is in destination-asset units (USDT) — roughly USD when toCcy is a stable.
        if (dto.Data.To.Min > 0 && IsUsdStable(toCcy))
        {
            lock (_apiMinAmountLock)
                _apiMinAmountUsd = dto.Data.To.Min;
        }

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.Data.To.Amount, // USDT per 1 XMR
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // =========================
    // BUY: ? USDT → 1 XMR
    // direction="to", amount="1" — data.from.amount is the USDT we'd send.
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var fromCcy = ResolveCode(query.Quote); // pay USDT
        var toCcy = ResolveCode(query.Base);    // receive XMR
        if (string.IsNullOrWhiteSpace(fromCcy) || string.IsNullOrWhiteSpace(toCcy)) return null;

        var dto = await PostPriceAsync(
            type: "float",
            fromCcy: fromCcy,
            toCcy: toCcy,
            direction: "to",
            amount: 1m,
            ct: ct);

        if (dto?.Data?.From is null) return null;
        if (dto.Code != 0) return null;
        if (dto.Data.From.Amount <= 0) return null;

        // from is USDT here, so from.min is already approximately USD.
        if (dto.Data.From.Min > 0 && IsUsdStable(fromCcy))
        {
            lock (_apiMinAmountLock)
                _apiMinAmountUsd = dto.Data.From.Min;
        }

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.Data.From.Amount, // USDT to send per 1 XMR
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // =========================
    // CURRENCIES: POST /v1/currencies with body "{}"
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        lock (_currenciesLock)
        {
            if (_cachedCurrencies is not null &&
                (DateTimeOffset.UtcNow - _currenciesAtUtc).TotalSeconds < Math.Max(30, opt.CurrenciesCacheSeconds))
            {
                return _cachedCurrencies;
            }
        }

        const string emptyBody = "{}";
        var res = await SendAsync(() => CreateRequest("v1/currencies", emptyBody), ct);

        if (res is null) return Array.Empty<ExchangeCurrency>();
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices)
            return Array.Empty<ExchangeCurrency>();

        var list = ParseCurrencies(res.Body);
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

    private static List<ExchangeCurrency> ParseCurrencies(string raw)
    {
        try
        {
            var env = JsonSerializer.Deserialize<CurrenciesResponse>(raw, JsonOpts);
            if (env is null || env.Code != 0 || env.Data is null) return new List<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>(env.Data.Count);

            foreach (var c in env.Data)
            {
                if (string.IsNullOrWhiteSpace(c.Code)) continue;
                // Only list currencies the operator currently sends out.
                if (!c.Send) continue;

                var ticker = !string.IsNullOrWhiteSpace(c.Coin) ? c.Coin!.Trim().ToUpperInvariant()
                                                                : c.Code!.Trim().ToUpperInvariant();
                var network = string.IsNullOrWhiteSpace(c.Network)
                    ? (c.Code!.Equals(ticker, StringComparison.OrdinalIgnoreCase) ? "Mainnet" : "")
                    : c.Network!.Trim();

                list.Add(new ExchangeCurrency(
                    ExchangeId: c.Code!.Trim().ToLowerInvariant(),
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

    // =========================
    // PRICE CALL
    // =========================
    private async Task<PriceResponse?> PostPriceAsync(
        string type, string fromCcy, string toCcy, string direction, decimal amount, CancellationToken ct)
    {
        // Serialize ONCE — sign over and send exactly these bytes.
        var bodyJson = SerializePriceRequest(type, fromCcy, toCcy, direction, amount);

        var res = await SendAsync(() => CreateRequest("v1/price", bodyJson), ct);

        if (res is null) return null;
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices) return null;

        try { return JsonSerializer.Deserialize<PriceResponse>(res.Body, JsonOpts); }
        catch { return null; }
    }

    /// <summary>
    /// Builds the /v1/price body deterministically. Order, casing, and whitespace
    /// here are what we sign and what we send — they must match byte-for-byte.
    /// </summary>
    private string SerializePriceRequest(
        string type, string fromCcy, string toCcy, string direction, decimal amount)
    {
        var amountStr = amount.ToString("0.########", CultureInfo.InvariantCulture);

        var req = new PriceRequest
        {
            Type = type,
            FromCcy = fromCcy,
            ToCcy = toCcy,
            Direction = direction,
            Amount = amountStr,
            Afftax = (IsConfigured() && opt.Afftax > 0) ? opt.Afftax : (int?)null
        };

        return JsonSerializer.Serialize(req, JsonOpts);
    }

    // =========================
    // HTTP / SIGNING
    // =========================
    private HttpRequestMessage CreateRequest(string path, string bodyJson)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Suppress Expect: 100-continue — same defensive lesson from the GhostSwap
        // debugging. Cheap proxies sometimes never reply 100 and the POST stalls.
        req.Headers.ExpectContinue = false;

        if (req.Headers.UserAgent.Count == 0 && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);

        if (IsConfigured())
        {
            var sig = ComputeHmacHex(opt.ApiSecret!, bodyJson);
            var nonce = NewNonceHex(16);

            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("X-API-SIGN", sig);
            req.Headers.TryAddWithoutValidation("X-API-NONCE", nonce);
        }

        // The same UTF-8 string we just signed.
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        return req;
    }

    private static string ComputeHmacHex(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NewNonceHex(int byteCount)
    {
        Span<byte> buf = stackalloc byte[16];
        if (byteCount != buf.Length) buf = new byte[byteCount];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private Task<SafeHttp.Result?> SendAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        return http.SendForStringWithRetryAsync(requestFactory, timeout, Math.Clamp(opt.RetryCount, 0, 6), ct);
    }

    private bool IsConfigured()
        => !string.IsNullOrWhiteSpace(opt.ApiKey) && !string.IsNullOrWhiteSpace(opt.ApiSecret);

    // =========================
    // CODE RESOLUTION (AssetRef → 0trace code)
    //
    // 0trace uses {ticker}_{network_short} for multi-network coins/tokens
    // (usdt_trc20, usdc_arb, eth_bsc, dai_eth, ...) and bare lowercase
    // tickers for single-chain natives (btc, xmr, sol, bnb, trx).
    // =========================
    private static string ResolveCode(AssetRef asset)
    {
        var ex = (asset.ExchangeId ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ex)) return ex;

        var ticker = (asset.Ticker ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(ticker)) return "";

        // Multi-network tickers at 0trace: stablecoins, native ETH, wrapped BTC variants.
        // Each requires a {ticker}_{network_short} suffix — bare ticker is not a valid code.
        if (ticker is "usdt" or "usdc" or "eth" or "dai" or "wbtc" or "btcb")
        {
            var net = NetworkSlug(asset.Network);
            if (string.IsNullOrEmpty(net)) return "";   // unknown network → fail-loud
            return $"{ticker}_{net}";
        }

        return ticker;
    }

    private static string NetworkSlug(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return "";

        // 0trace canonical network slugs. Networks NOT supported by 0trace today:
        // Polygon, Optimism, Avalanche C-Chain, Base. Synthesising codes for them
        // only produces 400 INVALID_REQUEST — return "" so ResolveCode collapses
        // to "" instead of inventing a dead code.
        return network.Trim().ToLowerInvariant() switch
        {
            "tron" or "trc20" or "trx"                    => "trc20",
            "ethereum" or "erc20" or "eth" or "mainnet"   => "eth",
            "binance smart chain" or "bsc" or
            "bep20" or "binance"                          => "bsc",
            "solana" or "sol"                             => "sol",
            "arbitrum" or "arb" or "arbitrum one"         => "arb",
            _                                             => ""
        };
    }

    private static bool IsUsdStable(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var c = code.ToLowerInvariant();
        return c == "usdt" || c == "usdc" || c == "dai"
            || c.StartsWith("usdt_") || c.StartsWith("usdc_") || c.StartsWith("dai_");
    }

    // =========================
    // DTOs
    // =========================
    private sealed class PriceRequest
    {
        [JsonPropertyName("type")]      public string Type { get; set; } = "float";
        [JsonPropertyName("fromCcy")]   public string FromCcy { get; set; } = "";
        [JsonPropertyName("toCcy")]     public string ToCcy { get; set; } = "";
        [JsonPropertyName("direction")] public string Direction { get; set; } = "from";
        [JsonPropertyName("amount")]    public string Amount { get; set; } = "0";
        [JsonPropertyName("afftax")]    public int? Afftax { get; set; }
    }

    private sealed class PriceResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")]  public string? Msg { get; set; }
        [JsonPropertyName("data")] public PriceData? Data { get; set; }
    }

    private sealed class PriceData
    {
        [JsonPropertyName("from")]      public PriceSide? From { get; set; }
        [JsonPropertyName("to")]        public PriceSide? To { get; set; }
        [JsonPropertyName("markupBps")] public int MarkupBps { get; set; }
    }

    private sealed class PriceSide
    {
        [JsonPropertyName("code")]      public string? Code { get; set; }
        [JsonPropertyName("coin")]      public string? Coin { get; set; }
        [JsonPropertyName("network")]   public string? Network { get; set; }
        [JsonPropertyName("amount")]    public decimal Amount { get; set; }
        [JsonPropertyName("rate")]      public decimal Rate { get; set; }
        [JsonPropertyName("precision")] public int Precision { get; set; }
        [JsonPropertyName("min")]       public decimal Min { get; set; }
        [JsonPropertyName("max")]       public decimal Max { get; set; }
        [JsonPropertyName("usd")]       public decimal Usd { get; set; }
    }

    private sealed class CurrenciesResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")]  public string? Msg { get; set; }
        [JsonPropertyName("data")] public List<CurrencyItem>? Data { get; set; }
    }

    private sealed class CurrencyItem
    {
        [JsonPropertyName("code")]      public string? Code { get; set; }
        [JsonPropertyName("coin")]      public string? Coin { get; set; }
        [JsonPropertyName("network")]   public string? Network { get; set; }
        [JsonPropertyName("name")]      public string? Name { get; set; }
        [JsonPropertyName("recv")]      public bool Recv { get; set; }
        [JsonPropertyName("send")]      public bool Send { get; set; }
        [JsonPropertyName("precision")] public int Precision { get; set; }
    }
}