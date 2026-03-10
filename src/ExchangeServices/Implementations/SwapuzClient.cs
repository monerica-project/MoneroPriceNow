using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class SwapuzClient : ISwapuzClient
{
    private static readonly JsonSerializerOptions SwapuzJsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private const string XmrNetwork = "XMR";
    private const string UsdtNetwork = "TRX";

    private readonly HttpClient _http;
    private readonly SwapuzOptions _opt;

    public string ExchangeKey => "swapuz";
    public string SiteName => _opt.SiteName;
    public string? SiteUrl => _opt.SiteUrl;
    public char PrivacyLevel => _opt.PrivacyLevel;

    public SwapuzClient(HttpClient http, IOptions<SwapuzOptions> options)
    {
        _http = http;
        _opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> ? USDT
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (fromTicker, fromNet) = ResolveTickerNetwork(query.Base);
        var (toTicker, toNet) = ResolveTickerNetwork(query.Quote);

        Console.WriteLine($"[SWAPUZ SELL] from={fromTicker}/{fromNet}, to={toTicker}/{toNet}");

        var rate = await GetRateAsync(fromTicker, fromNet, toTicker, toNet, 1m, ct);
        if (rate is null || rate.Rate <= 0)
        {
            Console.WriteLine("[SWAPUZ SELL] rate null or zero");
            return null;
        }

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: rate.Rate,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // =========================
    // BUY: ? USDT needed to receive 1 XMR
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (usdtTicker, usdtNet) = ResolveTickerNetwork(query.Quote);
        var (xmrTicker, xmrNet) = ResolveTickerNetwork(query.Base);

        Console.WriteLine($"[SWAPUZ BUY] from={usdtTicker}/{usdtNet}, to={xmrTicker}/{xmrNet}");

        var rate = await GetRateAsync(usdtTicker, usdtNet, xmrTicker, xmrNet, 200m, ct);
        if (rate is null || rate.Result <= 0)
        {
            Console.WriteLine("[SWAPUZ BUY] rate null or result zero");
            return null;
        }

        var usdtPerXmr = rate.Amount / rate.Result;
        Console.WriteLine($"[SWAPUZ BUY] probe={rate.Amount}, xmrOut={rate.Result}, usdtPerXmr={usdtPerXmr}");

        if (usdtPerXmr <= 0) return null;

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

    // =========================
    // CURRENCIES
    // GET /api/home/v1/coins
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/home/v1/coins");
        AddHeaders(req);

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[SWAPUZ CURRENCIES] Status={res?.Status}, Body={res?.Body?[..Math.Min(300, res?.Body?.Length ?? 0)]}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
            return Array.Empty<ExchangeCurrency>();

        var dto = JsonSerializer.Deserialize<SwapuzCoinsResponse>(res.Body, SwapuzJsonOpt);
        if (dto?.Result is null || dto.Result.Count == 0)
            return Array.Empty<ExchangeCurrency>();

        var currencies = new List<ExchangeCurrency>();
        foreach (var coin in dto.Result)
        {
            if (string.IsNullOrWhiteSpace(coin.ShortName)) continue;
            var ticker = coin.ShortName.Trim().ToUpperInvariant();

            if (coin.Network is null || coin.Network.Count == 0)
            {
                currencies.Add(new ExchangeCurrency(ticker, ticker, "Mainnet"));
                continue;
            }

            foreach (var net in coin.Network)
            {
                if (string.IsNullOrWhiteSpace(net.ShortName)) continue;
                if (!net.IsDeposit && !net.IsWithdraw) continue;

                var netShort = net.ShortName.Trim().ToUpperInvariant();
                currencies.Add(new ExchangeCurrency(
                    ExchangeId: $"{ticker}:{netShort}",
                    Ticker: ticker,
                    Network: net.FullName ?? net.Name ?? netShort
                ));
            }
        }

        return currencies.OrderBy(x => x.Ticker).ThenBy(x => x.Network).ToList();
    }

    // =========================
    // GET RATE
    // GET /api/home/v1/rate/?from=XMR&to=USDT&amount=1&fromNetwork=XMR&toNetwork=TRX&mode=float
    // =========================
    private async Task<SwapuzRateResult?> GetRateAsync(
        string from, string fromNetwork,
        string to, string toNetwork,
        decimal amount,
        CancellationToken ct)
    {
        var amountStr = amount.ToString("0.########", CultureInfo.InvariantCulture);
        var url = $"/api/home/v1/rate/?from={from}&to={to}&amount={amountStr}&fromNetwork={fromNetwork}&toNetwork={toNetwork}&mode=float";

        Console.WriteLine($"[SWAPUZ RATE] GET {url}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(req);

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[SWAPUZ RATE] Status={res?.Status}, Body={res?.Body}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        var dto = JsonSerializer.Deserialize<SwapuzRateResponse>(res.Body, SwapuzJsonOpt);

        Console.WriteLine($"[SWAPUZ RATE] dto.Status={dto?.Status}, Rate={dto?.Result?.Rate}, Result={dto?.Result?.Result}");

        if (dto is null || dto.Status != 200 || dto.Result is null) return null;

        return dto.Result;
    }

    // =========================
    // HEADERS
    // GET requests only need Accept + Api-key.
    // Do NOT add Content-Type to GET requests — it's a content header
    // and causes HttpClient to throw or silently break the request.
    // =========================
    private void AddHeaders(HttpRequestMessage req)
    {
        // Accept is a request header — safe to add via TryAddWithoutValidation
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
        {
            req.Headers.TryAddWithoutValidation("Api-key", _opt.ApiKey);
            Console.WriteLine($"[SWAPUZ] Api-key={_opt.ApiKey[..Math.Min(6, _opt.ApiKey.Length)]}***");
        }
        else
        {
            Console.WriteLine("[SWAPUZ] WARNING: ApiKey empty — sending unauthenticated");
        }
    }

    // =========================
    // HELPERS
    // =========================
    private static (string Ticker, string Network) ResolveTickerNetwork(AssetRef asset)
    {
        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        var net = (asset.Network ?? "").Trim().ToUpperInvariant();

        if (ticker == "XMR") return ("XMR", XmrNetwork);
        if (ticker == "USDT") return ("USDT", UsdtNetwork);

        if (!string.IsNullOrWhiteSpace(asset.ExchangeId))
        {
            var parts = asset.ExchangeId.Split(':', 2,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                return (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());
        }

        return (ticker, string.IsNullOrWhiteSpace(net) ? ticker : net);
    }

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(_opt.RequestTimeoutSeconds, 2, 60));

    // =========================
    // DTOs
    // =========================
    private sealed class SwapuzCoinsResponse
    {
        [JsonPropertyName("result")] public List<SwapuzCoinItem>? Result { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
    }

    private sealed class SwapuzCoinItem
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("shortName")] public string? ShortName { get; set; }
        [JsonPropertyName("isDeposit")] public bool IsDeposit { get; set; }
        [JsonPropertyName("isWithdraw")] public bool IsWithdraw { get; set; }
        [JsonPropertyName("network")] public List<SwapuzNetworkItem>? Network { get; set; }
    }

    private sealed class SwapuzNetworkItem
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("shortName")] public string? ShortName { get; set; }
        [JsonPropertyName("fullName")] public string? FullName { get; set; }
        [JsonPropertyName("isDeposit")] public bool IsDeposit { get; set; }
        [JsonPropertyName("isWithdraw")] public bool IsWithdraw { get; set; }
        [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
        [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    }

    private sealed class SwapuzRateResponse
    {
        [JsonPropertyName("result")] public SwapuzRateResult? Result { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
    }

    private sealed class SwapuzRateResult
    {
        [JsonPropertyName("result")] public decimal Result { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("rate")] public decimal Rate { get; set; }
        [JsonPropertyName("withdrawFee")] public decimal WithdrawFee { get; set; }
        [JsonPropertyName("minAmount")] public decimal MinAmount { get; set; }
        [JsonPropertyName("maxAmount")] public decimal MaxAmount { get; set; }
    }
}