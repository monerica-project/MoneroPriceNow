using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

/// <summary>
/// BitcoinVN exchange client.
///
/// Endpoint: GET /api/pairs/{depositMethodId}/{settleMethodId}
///   Response: { rate, min, max }
///   rate = settle units received per 1 unit deposited, post-fee.
///
/// Sell (XMR → USDT):
///   deposit = xmrbalance   settle = usdttrc20
///   rate is already USDT per 1 XMR → use directly as sell price.
///
/// Buy (USDT → XMR):
///   deposit = usdttrc20    settle = xmrbalance
///   rate = XMR per 1 USDT → buy price (USDT per 1 XMR) = 1 / rate
///
/// Transfer method IDs are hardcoded from a verified /api/info lookup:
///   XMR        → xmrbalance
///   BTC        → btcbalance
///   USDT/Tron  → usdttrc20
///   USDT/ETH   → usdterc20
///   USDT/BSC   → usdtbep20
///   USDT/SOL   → usdtsol
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

    public string ExchangeKey => "bitcoinvn";
    public string SiteName => _opt.SiteName;
    public string? SiteUrl => _opt.SiteUrl;

    public char PrivacyLevel => _opt.PrivacyLevel;

    public BitcoinVNClient(HttpClient http, IOptions<BitcoinVNOptions> options)
    {
        _http = http;
        _opt = options.Value;
    }

    // ─── Sell: 1 XMR → USDT ────────────────────────────────────────────────
    // deposit = base (XMR), settle = quote (USDT)
    // rate = settle per 1 deposit unit = USDT per XMR = direct sell price
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var depositId = MethodId(query.Base);
        var settleId = MethodId(query.Quote);
        if (depositId is null || settleId is null) return null;

        var dto = await FetchPairAsync(depositId, settleId, ct);
        if (dto is null || dto.Rate <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.Rate,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"sell {depositId}/{settleId} rate={dto.Rate}");
    }

    // ─── Buy: USDT → 1 XMR ─────────────────────────────────────────────────
    // deposit = quote (USDT), settle = base (XMR)
    // rate = XMR per 1 USDT → buy price (USDT per 1 XMR) = 1 / rate
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var depositId = MethodId(query.Quote);  // spending USDT
        var settleId = MethodId(query.Base);   // receiving XMR
        if (depositId is null || settleId is null) return null;

        var dto = await FetchPairAsync(depositId, settleId, ct);
        if (dto is null || dto.Rate <= 0) return null;

        var buyPrice = 1m / dto.Rate;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: buyPrice,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"buy {depositId}/{settleId} rate={dto.Rate} buyPrice={buyPrice:F6}");
    }

    // ─── Currencies (not used for price resolution) ─────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ─── HTTP ───────────────────────────────────────────────────────────────
    private async Task<PairDto?> FetchPairAsync(string depositId, string settleId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/pairs/{depositId}/{settleId}");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
            req.Headers.TryAddWithoutValidation("X-API-KEY", _opt.ApiKey);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_opt.RequestTimeoutSeconds, 2, 30));
        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, timeout, ct);

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
        {
            Console.WriteLine($"[BitcoinVN] pairs/{depositId}/{settleId} failed: status={res?.Status}");
            return null;
        }

        return JsonSerializer.Deserialize<PairDto>(res.Body, JsonOpt);
    }

    // ─── Transfer method ID lookup ──────────────────────────────────────────
    // IDs verified from GET /api/info (209 methods).
    private static string? MethodId(AssetRef asset)
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
                "Tron" => "usdttrc20",
                "Ethereum" => "usdterc20",
                "Binance Smart Chain" => "usdtbep20",
                "Solana" => "usdtsol",
                _ => "usdttrc20",   // default to TRC20
            },
            _ => null,
        };
    }

    // ─── DTOs ───────────────────────────────────────────────────────────────
    private sealed class PairDto
    {
        [JsonPropertyName("rate")] public decimal Rate { get; set; }
        [JsonPropertyName("min")] public decimal Min { get; set; }
        [JsonPropertyName("max")] public decimal Max { get; set; }
    }
}