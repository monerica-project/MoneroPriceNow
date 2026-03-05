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

    private readonly HttpClient http;
    private readonly EtzSwapOptions opt;

    public string  ExchangeKey => "etzswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;

    public EtzSwapClient(HttpClient http, IOptions<EtzSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // =========================
    // SELL: send 1 XMR → read amountTo (USDT) directly
    // amount=1 XMR is above minimum and matches website quote
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (coinFrom, networksFrom) = ResolveCoinAndNetworks(query.Base);
        var (coinTo, networksTo) = ResolveCoinAndNetworks(query.Quote);

        if (string.IsNullOrWhiteSpace(coinFrom) || string.IsNullOrWhiteSpace(coinTo))
            return null;

        foreach (var netFrom in networksFrom)
            foreach (var netTo in networksTo)
            {
                var dto = await GetRateAsync(
                    coinFrom: coinFrom,
                    networkFrom: netFrom,
                    coinTo: coinTo,
                    networkTo: netTo,
                    rateType: "float",
                    amountFrom: 1m,      // send 1 XMR
                    amountTo: null,
                    ct: ct
                );

                if (dto?.Data is null) continue;

                // amountTo / amountFrom = USDT per XMR (normalises any rounding)
                var fromAmt = dto.Data.AmountFrom;
                var toAmt = dto.Data.AmountTo;
                if (fromAmt <= 0 || toAmt <= 0) continue;

                var usdtPerXmr = toAmt / fromAmt;
                if (usdtPerXmr <= 0) continue;

                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: usdtPerXmr,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }

        return null;
    }

    // =========================
    // BUY: ? USDT → 1 XMR
    // Probe with amountFrom=500 USDT, read amountTo (XMR), divide:
    //   USDT per 1 XMR = amountFrom / amountTo
    //
    // DO NOT use amountTo=1 approach — the API returns a distorted amountFrom
    // (cost for slightly >1 XMR) which inflates the displayed buy price.
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (coinFrom, networksFrom) = ResolveCoinAndNetworks(query.Quote); // USDT
        var (coinTo, networksTo) = ResolveCoinAndNetworks(query.Base);  // XMR

        if (string.IsNullOrWhiteSpace(coinFrom) || string.IsNullOrWhiteSpace(coinTo))
            return null;

        const decimal probeUsdt = 500m;

        foreach (var netFrom in networksFrom)
            foreach (var netTo in networksTo)
            {
                var dto = await GetRateAsync(
                    coinFrom: coinFrom,
                    networkFrom: netFrom,
                    coinTo: coinTo,
                    networkTo: netTo,
                    rateType: "float",
                    amountFrom: probeUsdt,   // ← send 500 USDT
                    amountTo: null,         // ← never use amountTo; distorts rate
                    ct: ct
                );

                if (dto?.Data is null) continue;

                // amountFrom = USDT used (≈500), amountTo = XMR received
                var fromAmt = dto.Data.AmountFrom;
                var toAmt = dto.Data.AmountTo;
                if (fromAmt <= 0 || toAmt <= 0) continue;

                // USDT per 1 XMR = fromAmt / toAmt
                var usdtPerXmr = fromAmt / toAmt;
                if (usdtPerXmr <= 0) continue;

                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: usdtPerXmr,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }

        return null;
    }

    // =========================
    // CURRENCIES
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var urlsToTry = new[]
        {
            "/api/v1/deposit/public/coins?page=1&limit=250",
            "/api/v1/deposit/public/coins?page=1&size=250",
            "/api/v1/deposit/public/coins?limit=250&offset=0",
            "/api/v1/deposit/public/coins"
        };

        foreach (var url in urlsToTry)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuthHeaders(req);

                using var resp = await http.SendAsync(req, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode) continue;

                var list = ParseCoins(raw);
                if (list.Count > 0) return list;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) { return Array.Empty<ExchangeCurrency>(); }
        }

        return Array.Empty<ExchangeCurrency>();
    }

    // =========================
    // RATE CALL
    // =========================
    private async Task<RateResponse?> GetRateAsync(
        string coinFrom,
        string networkFrom,
        string coinTo,
        string networkTo,
        string rateType,
        decimal? amountFrom,
        decimal? amountTo,
        CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"coinFrom={Uri.EscapeDataString(coinFrom)}",
            $"networkFrom={Uri.EscapeDataString(networkFrom)}",
            $"coinTo={Uri.EscapeDataString(coinTo)}",
            $"networkTo={Uri.EscapeDataString(networkTo)}",
            $"rateType={Uri.EscapeDataString(rateType)}"
        };

        if (amountFrom is not null)
            qs.Add($"amountFrom={amountFrom.Value.ToString(CultureInfo.InvariantCulture)}");

        if (amountTo is not null)
            qs.Add($"amountTo={amountTo.Value.ToString(CultureInfo.InvariantCulture)}");

        var url = "/api/v1/deposit/public/rate?" + string.Join("&", qs);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthHeaders(req);

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode) return null;

            return JsonSerializer.Deserialize<RateResponse>(raw, JsonOpt);
        }
        catch (HttpRequestException) { return null; }
        catch (OperationCanceledException) { return null; }
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

    private static (string Coin, string[] NetworksToTry) ResolveCoinAndNetworks(AssetRef asset)
    {
        var coin = (asset.ExchangeId ?? asset.Ticker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(coin)) return ("", Array.Empty<string>());

        var net = ToEtzNetwork(asset.Ticker ?? "", asset.Network);

        // For Tron, try multiple common network codes
        if (asset.Network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) == true ||
            net.Equals("TRX", StringComparison.OrdinalIgnoreCase) ||
            net.Equals("TRON", StringComparison.OrdinalIgnoreCase))
        {
            return (coin, new[] { "TRX", "TRC20", "TRON" }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        return (coin, new[] { net });
    }

    private static string ToEtzNetwork(string ticker, string? network)
    {
        if (string.IsNullOrWhiteSpace(network))
            return ticker.Trim().ToUpperInvariant();

        return network.Trim() switch
        {
            "Mainnet" => ticker.Trim().ToUpperInvariant(),
            "Tron" => "TRX",
            "Ethereum" => "ETH",
            "Binance Smart Chain" => "BSC",
            "Solana" => "SOL",
            "Arbitrum" => "ARBITRUM",
            "Base" => "BASE",
            "Polygon" => "MATIC",
            var n => n.ToUpperInvariant()
        };
    }

    private static List<ExchangeCurrency> ParseCoins(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var dataEl) &&
                dataEl.ValueKind == JsonValueKind.Array)
                arr = dataEl;
            else if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else
                return new List<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>();

            foreach (var item in arr.EnumerateArray())
            {
                var code =
                    (item.TryGetProperty("code", out var c1) ? c1.GetString() : null) ??
                    (item.TryGetProperty("ticker", out var c2) ? c2.GetString() : null) ??
                    (item.TryGetProperty("symbol", out var c3) ? c3.GetString() : null);

                if (string.IsNullOrWhiteSpace(code)) continue;

                var network =
                    (item.TryGetProperty("network", out var n1) ? n1.GetString() : null) ??
                    (item.TryGetProperty("networkCode", out var n2) ? n2.GetString() : null);

                var netLabel = NormalizeNetworkLabel(code!, network);

                list.Add(new ExchangeCurrency(
                    ExchangeId: code!.Trim().ToUpperInvariant(),
                    Ticker: code!.Trim().ToUpperInvariant(),
                    Network: netLabel
                ));
            }

            return list
                .OrderBy(x => x.Ticker)
                .ThenBy(x => x.Network)
                .ToList();
        }
        catch { return new List<ExchangeCurrency>(); }
    }

    private static string NormalizeNetworkLabel(string ticker, string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return "Mainnet";

        var n = network.Trim().ToUpperInvariant();

        return n switch
        {
            "TRX" or "TRON" or "TRC20" => "Tron",
            "ETH" or "ERC20" => "Ethereum",
            "BSC" => "Binance Smart Chain",
            "SOL" => "Solana",
            "ARBITRUM" => "Arbitrum",
            "BASE" => "Base",
            "MATIC" or "POLYGON" => "Polygon",
            _ when n == ticker.Trim().ToUpperInvariant() => "Mainnet",
            _ => n
        };
    }

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