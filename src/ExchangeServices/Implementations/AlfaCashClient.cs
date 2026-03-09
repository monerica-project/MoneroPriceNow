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
/// Alfa.Cash API client.
///
/// Endpoints used:
///   POST /api/rate.json    → current exchange rate for a pair
///   GET  /api/getcoins     → supported currencies (keyed by gate name)
///
/// Auth: none required for rate or currency endpoints.
///
/// Rate semantics:
///   rate.json returns { "rate": X } where X = units of gate_withdrawal received per 1 unit of gate_deposit.
///
///   Sell (XMR → USDT): gate_deposit=monero,   gate_withdrawal=trc20usdt → rate = USDT per 1 XMR  (direct)
///   Buy  (USDT → XMR): gate_deposit=trc20usdt, gate_withdrawal=monero   → rate = XMR per 1 USDT → buyPrice = 1 / rate
/// </summary>
public sealed class AlfaCashClient : IAlfaCashClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly AlfaCashOptions _opt;

    public string  ExchangeKey => "alfacash";
    public string  SiteName    => _opt.SiteName;
    public string? SiteUrl     => _opt.SiteUrl;

    public AlfaCashClient(HttpClient http, IOptions<AlfaCashOptions> options)
    {
        _http = http;
        _opt  = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> ? USDT
    // POST /api/rate.json { gate_deposit=monero, gate_withdrawal=trc20usdt }
    // rate = USDT received per 1 XMR sent (direct sell price)
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var depositGate    = ToGate(query.Base);
        var withdrawalGate = ToGate(query.Quote);

        if (depositGate is null || withdrawalGate is null)
        {
            Console.WriteLine($"[ALFACASH SELL] Unsupported pair: {query.Base.Ticker}/{query.Quote.Ticker}");
            return null;
        }

        Console.WriteLine($"[ALFACASH SELL] gate_deposit={depositGate}, gate_withdrawal={withdrawalGate}");

        var dto = await GetRateAsync(depositGate, withdrawalGate, ct);
        if (dto is null || dto.Rate <= 0)
        {
            Console.WriteLine("[ALFACASH SELL] rate null or zero");
            return null;
        }

        Console.WriteLine($"[ALFACASH SELL] rate={dto.Rate}");

        return new PriceResult(
            Exchange:      ExchangeKey,
            Base:          query.Base,
            Quote:         query.Quote,
            Price:         dto.Rate,
            TimestampUtc:  DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw:           $"sell deposit={depositGate} withdrawal={withdrawalGate} rate={dto.Rate}"
        );
    }

    // =========================
    // BUY: ? USDT needed to receive 1 XMR
    // POST /api/rate.json { gate_deposit=trc20usdt, gate_withdrawal=monero }
    // rate = XMR received per 1 USDT → buyPrice = 1 / rate
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var depositGate    = ToGate(query.Quote); // USDT
        var withdrawalGate = ToGate(query.Base);  // XMR

        if (depositGate is null || withdrawalGate is null)
        {
            Console.WriteLine($"[ALFACASH BUY] Unsupported pair: {query.Quote.Ticker}/{query.Base.Ticker}");
            return null;
        }

        Console.WriteLine($"[ALFACASH BUY] gate_deposit={depositGate}, gate_withdrawal={withdrawalGate}");

        var dto = await GetRateAsync(depositGate, withdrawalGate, ct);
        if (dto is null || dto.Rate <= 0)
        {
            Console.WriteLine("[ALFACASH BUY] rate null or zero");
            return null;
        }

        // rate = XMR per 1 USDT → invert to get USDT per 1 XMR
        var buyPrice = 1m / dto.Rate;
        Console.WriteLine($"[ALFACASH BUY] rawRate={dto.Rate}, buyPrice={buyPrice:F2}");

        return new PriceResult(
            Exchange:      ExchangeKey,
            Base:          query.Base,
            Quote:         query.Quote,
            Price:         buyPrice,
            TimestampUtc:  DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw:           $"buy deposit={depositGate} withdrawal={withdrawalGate} rate={dto.Rate} buyPrice={buyPrice:F6}"
        );
    }

    // =========================
    // CURRENCIES
    // GET /api/getcoins
    // Returns object keyed by gate name, each with currency code + network
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/getcoins");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);
        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
            return Array.Empty<ExchangeCurrency>();

        var dto = JsonSerializer.Deserialize<Dictionary<string, AlfaCashCoinItem>>(res.Body, JsonOpt);
        if (dto is null) return Array.Empty<ExchangeCurrency>();

        return dto
            .Where(kv => kv.Value.Deposit || kv.Value.Withdrawal)
            .Select(kv => new ExchangeCurrency(
                ExchangeId: kv.Key,              // gate name e.g. "trc20usdt"
                Ticker:     kv.Value.Currency ?? kv.Key.ToUpperInvariant(),
                Network:    kv.Value.Network  ?? kv.Value.Currency ?? kv.Key
            ))
            .OrderBy(x => x.Ticker)
            .ToList();
    }

    // =========================
    // PRIVATE: POST /api/rate.json
    // =========================
    private async Task<AlfaCashRateResult?> GetRateAsync(
        string gateDeposit, string gateWithdrawal,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            gate_deposit    = gateDeposit,
            gate_withdrawal = gateWithdrawal
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/rate.json")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[ALFACASH RATE] status={res?.Status} body={res?.Body?[..Math.Min(200, res?.Body?.Length ?? 0)]}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        var dto = JsonSerializer.Deserialize<AlfaCashRateResult>(res.Body, JsonOpt);
        return dto;
    }

    // =========================
    // HELPERS
    // =========================

    /// <summary>Maps an AssetRef to the Alfa.cash gate name.</summary>
    private static string? ToGate(AssetRef asset)
    {
        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        var net    = (asset.Network ?? "").Trim();

        return ticker switch
        {
            "XMR"  => "monero",
            "BTC"  => "bitcoin",
            "ETH"  => "ethereum",
            "LTC"  => "litecoin",
            "BNB"  => "smartchain",
            "DOGE" => "dogecoin",
            "SOL"  => "solana",
            "XRP"  => "xrp",
            "ADA"  => "cardano",
            "DOT"  => "polkadot",
            "TRX"  => "tron",
            "BCH"  => "bitcoincash",
            "USDT" => net switch
            {
                "Tron"                => "trc20usdt",
                "Ethereum"            => "tethererc20",
                "Binance Smart Chain" => "bep20usdt",
                _                    => "trc20usdt",   // default USDT to TRC20
            },
            "USDC" => "usdcoin",
            "DAI"  => "dai",
            "LINK" => "chainlink",
            "XLM"  => "stellar",
            "ATOM" => "cosmos",
            "DASH" => "dash",
            "ZEC"  => "zcash",
            _      => null,
        };
    }

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(_opt.RequestTimeoutSeconds, 2, 60));

    // =========================
    // DTOs
    // =========================
    private sealed class AlfaCashRateResult
    {
        [JsonPropertyName("gate_deposit")]    public string?  GateDeposit    { get; set; }
        [JsonPropertyName("gate_withdrawal")] public string?  GateWithdrawal { get; set; }
        [JsonPropertyName("pair")]            public string?  Pair           { get; set; }
        [JsonPropertyName("rate")]            public decimal  Rate           { get; set; }
        [JsonPropertyName("error")]           public string?  Error          { get; set; }
    }

    private sealed class AlfaCashCoinItem
    {
        [JsonPropertyName("currency")]   public string? Currency   { get; set; }
        [JsonPropertyName("title")]      public string? Title      { get; set; }
        [JsonPropertyName("network")]    public string? Network    { get; set; }
        [JsonPropertyName("deposit")]    public bool    Deposit    { get; set; }
        [JsonPropertyName("withdrawal")] public bool    Withdrawal { get; set; }
    }
}
