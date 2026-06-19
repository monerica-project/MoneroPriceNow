using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class EtzSwapClient : IEtzSwapClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Network preference for MULTI-network coins (e.g. USDT). For a price feed the chain
    // is immaterial (USDT ~= $1 on every chain), and Etz confirmed it does NOT pair
    // USDT-Tron(TRX) with XMR but DOES pair USDT-ETH/SOL/BSC with XMR. So we try broadly
    // routable networks first and let the first one that quotes win (then it's memoized).
    private static readonly string[] NetworkPreference =
        { "ETH", "SOL", "BSC", "MATIC", "ARBITRUM", "OPTIMISM", "TON", "CELO", "TRX" };

    private readonly HttpClient http;
    private readonly EtzSwapOptions opt;
    private decimal _liveMinAmountUsd;

    // Etz catalog: COIN -> its networks. The network "Key" is what the rate endpoint
    // wants (USDT -> "TRX"/"ETH"/..., XMR -> "XMR"). Loaded once.
    private IReadOnlyDictionary<string, List<NetInfo>>? _catalog;
    private readonly SemaphoreSlim _catalogLock = new(1, 1);

    // Memoizes the (networkFrom, networkTo) that returned 2xx for a coinFrom->coinTo,
    // so steady-state is ONE request per quote with no discovery noise.
    private readonly ConcurrentDictionary<string, (string From, string To)> _resolved = new();

    public string ExchangeKey => "etzswap";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    public decimal MinAmountUsd => _liveMinAmountUsd > 0 ? _liveMinAmountUsd : opt.MinAmountUsd;

    public EtzSwapClient(HttpClient http, IOptions<EtzSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    private sealed record NetInfo(string Key, string Short, string Full);

    // =========================
    // SELL: 1 XMR -> read amountTo (USDT)
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var data = await ResolveAndQuoteAsync(query.Base, query.Quote, amountFrom: 1m, ct);
        if (data is null) return null;

        var from = data.AmountFrom;
        var to = data.AmountTo;
        if (from <= 0 || to <= 0) return null;

        var usdtPerXmr = to / from;
        if (usdtPerXmr <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, usdtPerXmr, DateTimeOffset.UtcNow);
    }

    // =========================
    // BUY: probe 500 USDT -> read amountTo (XMR), divide. Caches USD minimum.
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        // from = USDT (query.Quote), to = XMR (query.Base)
        var data = await ResolveAndQuoteAsync(query.Quote, query.Base, amountFrom: query.ProbeAmount ?? 500m, ct);
        if (data is null) return null;

        if (data.MinAmountFrom > 0)
            _liveMinAmountUsd = data.MinAmountFrom;

        var from = data.AmountFrom;
        var to = data.AmountTo;
        if (from <= 0 || to <= 0) return null;

        var usdtPerXmr = from / to;
        if (usdtPerXmr <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, usdtPerXmr, DateTimeOffset.UtcNow);
    }

    // =========================
    // RESOLVE (from catalog) + QUOTE — memoized; steady-state is one request.
    // =========================
    private async Task<RateData?> ResolveAndQuoteAsync(
        AssetRef fromAsset, AssetRef toAsset, decimal amountFrom, CancellationToken ct)
    {
        var coinFrom = CoinCode(fromAsset);
        var coinTo = CoinCode(toAsset);
        if (coinFrom is null || coinTo is null) return null;

        var key = $"{coinFrom}->{coinTo}";

        // Fast path: reuse the network combo that worked last time.
        if (_resolved.TryGetValue(key, out var known))
        {
            var hit = await GetRateAsync(coinFrom, known.From, coinTo, known.To, amountFrom, ct);
            if (hit?.Data is not null) return hit.Data;
            _resolved.TryRemove(key, out _); // stale — re-discover
        }

        // Discovery: try candidate networks (catalog-driven, preference-ordered),
        // keep the first combo that returns a quote, and memoize it.
        var fromNets = await NetworkCandidatesAsync(fromAsset, ct);
        var toNets = await NetworkCandidatesAsync(toAsset, ct);

        foreach (var netFrom in fromNets)
            foreach (var netTo in toNets)
            {
                var dto = await GetRateAsync(coinFrom, netFrom, coinTo, netTo, amountFrom, ct);
                if (dto?.Data is null) continue;

                _resolved[key] = (netFrom, netTo);
                Console.WriteLine($"[ETZSWAP] resolved {coinFrom}/{netFrom} -> {coinTo}/{netTo}");
                return dto.Data;
            }

        return null;
    }

    // Ordered network tokens to try for an asset, sourced from Etz's own catalog.
    // Single-network coins (XMR) -> their one network. Multi-network coins (USDT) ->
    // all their networks, ordered by NetworkPreference so a broadly-routable chain is
    // tried first (Etz won't pair USDT-Tron with XMR, but USDT-ETH/SOL/BSC work).
    private async Task<List<string>> NetworkCandidatesAsync(AssetRef asset, CancellationToken ct)
    {
        var coin = CoinCode(asset) ?? "";
        var cat = await GetCatalogAsync(ct);

        var nets = cat.TryGetValue(coin, out var infos) && infos.Count > 0
            ? infos.Select(i => i.Key).Where(k => !string.IsNullOrWhiteSpace(k)).ToList()
            : new List<string>();

        if (nets.Count == 1) return nets;

        if (nets.Count == 0)
        {
            var fb = FallbackNetwork(asset);
            return fb is null ? new List<string>() : new List<string> { fb };
        }

        // Multi-network: order by preference, then any remaining catalog networks.
        return nets
            .OrderBy(n =>
            {
                var i = Array.FindIndex(NetworkPreference,
                    p => p.Equals(n, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? int.MaxValue : i;
            })
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FallbackNetwork(AssetRef asset)
    {
        var net = (asset.Network ?? "").Trim();
        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();

        if (string.IsNullOrEmpty(net) || net.Equals(ticker, StringComparison.OrdinalIgnoreCase))
            return ticker; // native: XMR -> "XMR"

        return net.ToUpperInvariant() switch
        {
            "TRON" or "TRC20" => "ETH",   // Etz doesn't route USDT-Tron to XMR; ETH does.
            "ETHEREUM" or "ERC20" => "ETH",
            "BINANCE SMART CHAIN" or "BEP20" => "BSC",
            "SOLANA" => "SOL",
            "POLYGON" => "MATIC",
            var x => x
        };
    }

    // =========================
    // CATALOG  (/coins, fetched once)
    // =========================
    private async Task<IReadOnlyDictionary<string, List<NetInfo>>> GetCatalogAsync(CancellationToken ct)
    {
        if (_catalog is not null) return _catalog;

        await _catalogLock.WaitAsync(ct);
        try
        {
            if (_catalog is not null) return _catalog;

            var raw = await FetchCoinsRawAsync(ct);
            var map = raw is null
                ? new Dictionary<string, List<NetInfo>>(StringComparer.OrdinalIgnoreCase)
                : ParseCatalog(raw);

            _catalog = map;

            string Nets(string c) => map.TryGetValue(c, out var l) && l.Count > 0
                ? string.Join(",", l.Select(x => x.Key)) : "(none)";
            Console.WriteLine($"[ETZSWAP] catalog: {map.Count} coins. USDT=[{Nets("USDT")}] XMR=[{Nets("XMR")}]");

            return _catalog;
        }
        catch (OperationCanceledException)
        {
            return new Dictionary<string, List<NetInfo>>(StringComparer.OrdinalIgnoreCase);
        }
        finally { _catalogLock.Release(); }
    }

    private async Task<string?> FetchCoinsRawAsync(CancellationToken ct)
    {
        var urls = new[]
        {
            "/api/v1/deposit/public/coins?page=1&limit=500",
            "/api/v1/deposit/public/coins"
        };

        foreach (var url in urls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthHeaders(req);
                using var resp = await http.SendAsync(req, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(raw))
                    return raw;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    // Real Etz shape: { "data": { "coins": [ { "name": "...", "networks": { "KEY": { shortName, fullName } } } ] } }
    private static Dictionary<string, List<NetInfo>> ParseCatalog(string raw)
    {
        var map = new Dictionary<string, List<NetInfo>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var dataEl))
            {
                if (dataEl.ValueKind == JsonValueKind.Object &&
                    dataEl.TryGetProperty("coins", out var coinsEl) &&
                    coinsEl.ValueKind == JsonValueKind.Array)
                    arr = coinsEl;
                else if (dataEl.ValueKind == JsonValueKind.Array)
                    arr = dataEl;
                else return map;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("coins", out var coinsEl2) &&
                     coinsEl2.ValueKind == JsonValueKind.Array)
                arr = coinsEl2;
            else if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else return map;

            foreach (var item in arr.EnumerateArray())
            {
                var name = Str(item, "name") ?? Str(item, "code") ?? Str(item, "ticker") ?? Str(item, "symbol");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var coin = name.Trim().ToUpperInvariant();

                var nets = new List<NetInfo>();

                if (item.TryGetProperty("networks", out var netsEl))
                {
                    if (netsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in netsEl.EnumerateObject())
                        {
                            var key = prop.Name?.Trim() ?? "";
                            if (key.Length == 0) continue;
                            var sShort = Str(prop.Value, "shortName") ?? key;
                            var sFull = Str(prop.Value, "fullName") ?? "";
                            nets.Add(new NetInfo(key, sShort, sFull));
                        }
                    }
                    else if (netsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var n in netsEl.EnumerateArray())
                        {
                            string? key = n.ValueKind == JsonValueKind.String
                                ? n.GetString()
                                : (Str(n, "shortName") ?? Str(n, "network") ?? Str(n, "code") ?? Str(n, "name"));
                            if (string.IsNullOrWhiteSpace(key)) continue;
                            nets.Add(new NetInfo(key.Trim(), Str(n, "shortName") ?? key.Trim(), Str(n, "fullName") ?? ""));
                        }
                    }
                }
                else
                {
                    var flat = Str(item, "network") ?? Str(item, "networkCode");
                    if (!string.IsNullOrWhiteSpace(flat))
                        nets.Add(new NetInfo(flat.Trim(), flat.Trim(), ""));
                }

                if (nets.Count > 0)
                    map[coin] = nets;
            }
        }
        catch { /* keep whatever parsed */ }
        return map;

        static string? Str(JsonElement e, string prop)
            => e.ValueKind == JsonValueKind.Object &&
               e.TryGetProperty(prop, out var v) &&
               v.ValueKind == JsonValueKind.String
               ? v.GetString()
               : null;
    }

    // =========================
    // CURRENCIES (from the same cached catalog)
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var cat = await GetCatalogAsync(ct);
        var list = new List<ExchangeCurrency>();
        foreach (var (coin, nets) in cat)
            foreach (var n in nets)
                list.Add(new ExchangeCurrency(
                    ExchangeId: coin,
                    Ticker: coin,
                    Network: NormalizeNetworkLabel(coin, n.Key)));

        return list
            .DistinctBy(x => (x.Ticker, x.Network))
            .OrderBy(x => x.Ticker)
            .ThenBy(x => x.Network)
            .ToList();
    }

    // =========================
    // RATE CALL — logs the real error body on non-2xx.
    // =========================
    private async Task<RateResponse?> GetRateAsync(
        string coinFrom, string networkFrom, string coinTo, string networkTo,
        decimal amountFrom, CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"coinFrom={Uri.EscapeDataString(coinFrom)}",
            $"networkFrom={Uri.EscapeDataString(networkFrom)}",
            $"coinTo={Uri.EscapeDataString(coinTo)}",
            $"networkTo={Uri.EscapeDataString(networkTo)}",
            "rateType=float",
            $"amountFrom={amountFrom.ToString(CultureInfo.InvariantCulture)}"
        };

        var url = "/api/v1/deposit/public/rate?" + string.Join("&", qs);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(req);

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine(
                    $"[ETZSWAP] {(int)resp.StatusCode} {coinFrom}/{networkFrom}->{coinTo}/{networkTo} body={Trim(raw)}");
                return null;
            }

            return JsonSerializer.Deserialize<RateResponse>(raw, JsonOpt);
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) { return null; }
        catch (JsonException ex)
        {
            Console.WriteLine($"[ETZSWAP] parse error: {ex.Message}");
            return null;
        }
    }

    // =========================
    // HELPERS
    // =========================
    private void AddAuthHeaders(HttpRequestMessage req)
    {
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
        if (!string.IsNullOrWhiteSpace(opt.ApiSecretKey))
            req.Headers.TryAddWithoutValidation("X-API-SECRET-KEY", opt.ApiSecretKey);
        if (!string.IsNullOrWhiteSpace(opt.ApiKeyVersion))
            req.Headers.TryAddWithoutValidation("X-API-KEY-VERSION", opt.ApiKeyVersion);
    }

    private static string? CoinCode(AssetRef a)
    {
        var c = (a.ExchangeId ?? a.Ticker ?? "").Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(c) ? null : c;
    }

    private static string NormalizeNetworkLabel(string ticker, string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return "Mainnet";

        var n = network.Trim().ToUpperInvariant();
        return n switch
        {
            "TRX" or "TRON" or "TRC20" => "Tron",
            "ETH" or "ERC20" => "Ethereum",
            "BSC" or "BEP20" => "Binance Smart Chain",
            "SOL" => "Solana",
            "ARBITRUM" or "ARB" => "Arbitrum",
            "BASE" => "Base",
            "MATIC" or "POLYGON" => "Polygon",
            "TON" => "TON",
            _ when n == ticker.Trim().ToUpperInvariant() => "Mainnet",
            _ => network.Trim()
        };
    }

    private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s[..300]);

    // =========================
    // DTOs
    // =========================
    private sealed class RateResponse
    {
        [JsonPropertyName("data")] public RateData? Data { get; set; }
        [JsonPropertyName("errors")] public JsonElement Errors { get; set; }
    }

    private sealed class RateData
    {
        [JsonPropertyName("amountFrom")] public decimal AmountFrom { get; set; }
        [JsonPropertyName("amountTo")] public decimal AmountTo { get; set; }
        [JsonPropertyName("minAmountFrom")] public decimal MinAmountFrom { get; set; }
        [JsonPropertyName("maxAmountFrom")] public decimal? MaxAmountFrom { get; set; }
        [JsonPropertyName("rate")] public decimal Rate { get; set; }
    }
}