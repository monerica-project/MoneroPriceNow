using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

/// <summary>
/// BitcoinVN API client (ChadShift platform).
///
/// Endpoints used:
///   GET /api/pairs/{depositMethodId}/{settleMethodId}
///       rate = settle units received per 1 deposit unit, post-fee.
///
///   GET /api/info
///       Discovers valid transfer method IDs (cached 4 hours).
///       Transfer method IDs are lowercase strings: "xmr", "btc", "usdt_trc20", etc.
///       They differ from asset tickers and must be resolved from /api/info.
///
/// Sell (1 XMR -> USDT):  deposit=xmr  settle=usdt_trc20  rate = USDT per XMR (direct price)
/// Buy  (USDT -> 1 XMR):  deposit=usdt_trc20  settle=xmr  rate = XMR per USDT -> buy price = 1/rate
///
/// No API key required for public price endpoints.
/// </summary>
public sealed class BitcoinVNClient : IBitcoinVNClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly BitcoinVNOptions _opt;

    // Transfer method ID cache from /api/info (4-hour TTL)
    private readonly SemaphoreSlim _infoLock = new(1, 1);
    private Dictionary<string, string>? _methodIds;
    private DateTime _methodIdsAt = DateTime.MinValue;
    private static readonly TimeSpan MethodIdTtl = TimeSpan.FromHours(4);

    // BitcoinVN applies an ~0.47% spread on top of the percentage fees already
    // baked into the /api/pairs rate. Empirically derived from site vs API comparison.
    // Sell: rate is ~0.47% too low  → multiply up
    // Buy:  1/rate is ~0.47% too low → multiply up
    private const decimal FeeCorrection = 1.0047m;

    public string ExchangeKey => "bitcoinvn";
    public string SiteName => _opt.SiteName;
    public string? SiteUrl => _opt.SiteUrl;

    public BitcoinVNClient(HttpClient http, IOptions<BitcoinVNOptions> options)
    {
        _http = http;
        _opt = options.Value;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SELL: 1 XMR -> quote
    //   deposit=base(XMR), settle=quote(USDT/BTC)
    //   rate = settle units received per 1 XMR = direct sell price
    // ════════════════════════════════════════════════════════════════════════
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var depositId = await ResolveMethodIdAsync(query.Base, ct);
        var settleId = await ResolveMethodIdAsync(query.Quote, ct);
        if (depositId is null || settleId is null) return null;

        var dto = await GetPairAsync(depositId, settleId, ct);
        if (dto is null || dto.Rate <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.Rate * FeeCorrection,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"sell deposit={depositId} settle={settleId} rate={dto.Rate} min={dto.Min}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // BUY: quote -> 1 XMR
    //   deposit=quote(USDT/BTC), settle=base(XMR)
    //   rate = XMR received per 1 quote unit
    //   buy price (quote per 1 XMR) = 1 / rate
    // ════════════════════════════════════════════════════════════════════════
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var depositId = await ResolveMethodIdAsync(query.Quote, ct);   // spending quote
        var settleId = await ResolveMethodIdAsync(query.Base, ct);   // receiving XMR
        if (depositId is null || settleId is null) return null;

        var dto = await GetPairAsync(depositId, settleId, ct);
        if (dto is null || dto.Rate <= 0) return null;

        var buyPrice = (1m / dto.Rate) * FeeCorrection;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: buyPrice,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"buy deposit={depositId} settle={settleId} rate={dto.Rate} buyPrice={buyPrice:F6}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CURRENCIES: GET /api/info -> transferMethods
    // ════════════════════════════════════════════════════════════════════════
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var ids = await GetCachedMethodIdsAsync(ct);
        if (ids is null) return Array.Empty<ExchangeCurrency>();

        return ids
            .Select(kv =>
            {
                var parts = kv.Key.Split('/', 2);
                var ticker = parts[0].ToUpperInvariant();
                var network = parts.Length > 1 ? parts[1] : "Mainnet";
                return new ExchangeCurrency(
                    ExchangeId: kv.Value,       // transfer method ID: "usdt_trc20", "xmr", …
                    Ticker: ticker,
                    Network: network);
            })
            .OrderBy(c => c.Ticker)
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Private: GET /api/pairs/{depositId}/{settleId}
    // ════════════════════════════════════════════════════════════════════════
    private async Task<PairDto?> GetPairAsync(string depositId, string settleId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/pairs/{depositId}/{settleId}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        AddApiKey(req);

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[BitcoinVN] pairs/{depositId}/{settleId} " +
                          $"status={res?.Status} body={res?.Body?[..Math.Min(200, res?.Body?.Length ?? 0)]}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        return JsonSerializer.Deserialize<PairDto>(res.Body, JsonOpt);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Private: AssetRef -> transfer method ID
    //   1. Try hardcoded well-known IDs (avoids /api/info round-trip for common pairs)
    //   2. Fall back to cached /api/info map
    // ════════════════════════════════════════════════════════════════════════
    private async Task<string?> ResolveMethodIdAsync(AssetRef asset, CancellationToken ct)
    {
        var hardcoded = HardcodedMethodId(asset);
        if (hardcoded is not null) return hardcoded;

        var ids = await GetCachedMethodIdsAsync(ct);
        return ids?.GetValueOrDefault(NormKey(asset));
    }

    /// <summary>Well-known transfer method IDs for pairs we actively query.</summary>
    private static string? HardcodedMethodId(AssetRef asset)
    {
        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        var net = (asset.Network ?? "").Trim();

        return ticker switch
        {
            "XMR" => "xmrbalance",
            "BTC" => "btcbalance",
            "ETH" => "eth",
            "USDT" => net switch
            {
                "Tron" => "usdttrc20",    // no underscore
                "Ethereum" => "usdterc20",
                "Binance Smart Chain" => "usdtbep20",
                "Solana" => "usdtsol",
                _ => "usdtbalance",  // internal balance fallback
            },
            _ => null,
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // Private: fetch and cache /api/info transfer method map
    // ════════════════════════════════════════════════════════════════════════
    private async Task<Dictionary<string, string>?> GetCachedMethodIdsAsync(CancellationToken ct)
    {
        if (_methodIds != null && DateTime.UtcNow - _methodIdsAt < MethodIdTtl)
            return _methodIds;

        await _infoLock.WaitAsync(ct);
        try
        {
            if (_methodIds != null && DateTime.UtcNow - _methodIdsAt < MethodIdTtl)
                return _methodIds;

            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/info");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            AddApiKey(req);

            var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);
            if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
                return _methodIds;

            using var doc = JsonDocument.Parse(res.Body);
            if (!doc.RootElement.TryGetProperty("transferMethods", out var methods))
                return _methodIds;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var method in methods.EnumerateObject())
            {
                var methodId = method.Name;   // e.g. "usdt_trc20", "xmr"
                var obj = method.Value;

                string? ticker = null;
                string network = "Mainnet";

                if (obj.TryGetProperty("asset", out var assetEl))
                    ticker = assetEl.GetString()?.ToUpperInvariant();

                if (obj.TryGetProperty("network", out var netEl))
                    network = NormalizeNetwork(netEl.GetString()) ?? "Mainnet";

                ticker ??= methodId.ToUpperInvariant();
                map[NormKeyRaw(ticker, network)] = methodId;
            }

            _methodIds = map;
            _methodIdsAt = DateTime.UtcNow;

            Console.WriteLine($"[BitcoinVN] Loaded {map.Count} transfer method IDs from /api/info");
            return _methodIds;
        }
        finally
        {
            _infoLock.Release();
        }
    }

    private static string NormKey(AssetRef asset) =>
        NormKeyRaw(
            (asset.Ticker ?? "").Trim().ToUpperInvariant(),
            (asset.Network ?? "Mainnet").Trim());

    private static string NormKeyRaw(string ticker, string network) =>
        $"{ticker}/{network}";

    private static string? NormalizeNetwork(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "tron" or "trx" or "trc20" => "Tron",
            "ethereum" or "eth" or "erc20" => "Ethereum",
            "bsc" or "binance smart chain" or "bnb" => "Binance Smart Chain",
            "solana" or "sol" => "Solana",
            "bitcoin" or "btc"
                or "monero" or "xmr"
                or "litecoin" or "ltc" => "Mainnet",
            _ => "Mainnet",
        };

    private void AddApiKey(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            req.Headers.TryAddWithoutValidation("X-API-KEY", _opt.ApiKey);
    }

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(_opt.RequestTimeoutSeconds, 2, 60));

    // ════════════════════════════════════════════════════════════════════════
    // DTOs
    // ════════════════════════════════════════════════════════════════════════

    private sealed class PairDto
    {
        /// <summary>Settle units received per 1 deposit unit (post-fee, including all fees).</summary>
        [JsonPropertyName("rate")] public decimal Rate { get; set; }
        [JsonPropertyName("min")] public decimal Min { get; set; }
        [JsonPropertyName("max")] public decimal Max { get; set; }
    }
}