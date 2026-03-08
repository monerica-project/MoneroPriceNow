using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class ChangeeClient : IChangeeClient
{
    private static readonly JsonSerializerOptions ChangeeJsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly ChangeeOptions _opt;

    public string ExchangeKey => "changee";
    public string SiteName => _opt.SiteName;
    public string? SiteUrl => _opt.SiteUrl;

    public ChangeeClient(HttpClient http, IOptions<ChangeeOptions> options)
    {
        _http = http;
        _opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> ? USDT
    // GET /v1/api/rate?from=XMR&to=USDT&amount=1
    // rate field = USDT received per 1 XMR sent
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = ResolveSymbol(query.Base);
        var to = ResolveSymbol(query.Quote);

        var dto = await GetRateAsync(from, to, 1m, ct);
        if (dto is null || dto.Result != true || dto.Rate <= 0) return null;

        Console.WriteLine($"[CHANGEE SELL] rate={dto.Rate}");

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.Rate,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // =========================
    // BUY: ? USDT needed to receive 1 XMR
    // GET /v1/api/rate?from=USDT&to=XMR&amount=200
    // rate field = total XMR received for the probe amount of USDT (NOT per-unit rate)
    // buyPrice = probeAmount / rate  e.g. 200 / 0.581 ≈ 344.23 USDT per XMR
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = ResolveSymbol(query.Quote); // USDT
        var to = ResolveSymbol(query.Base);  // XMR

        const decimal probeUsdt = 200m;
        var dto = await GetRateAsync(from, to, probeUsdt, ct);
        if (dto is null || dto.Result != true || dto.Rate <= 0) return null;

        // rate = XMR received for probeUsdt USDT → invert to get USDT per XMR
        var usdtPerXmr = probeUsdt / dto.Rate;

        Console.WriteLine($"[CHANGEE BUY] rawRate={dto.Rate}, probeUsdt={probeUsdt}, usdtPerXmr={usdtPerXmr:F2}");

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
    // GET /v1/api/currencies
    // Returns a flat dictionary keyed by ticker symbol
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var body = await GetAsync($"/v1/api/currencies?key={_opt.ApiKey}", ct);

        Console.WriteLine($"[CHANGEE CURRENCIES] Body={body?[..Math.Min(300, body?.Length ?? 0)]}");

        if (body is null) return Array.Empty<ExchangeCurrency>();

        var dto = JsonSerializer.Deserialize<Dictionary<string, ChangeeCurrencyItem>>(body, ChangeeJsonOpt);
        if (dto is null || dto.Count == 0) return Array.Empty<ExchangeCurrency>();

        return dto
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value.Available)
            .Select(kv => new ExchangeCurrency(
                ExchangeId: kv.Key.Trim().ToUpperInvariant(),
                Ticker: kv.Key.Trim().ToUpperInvariant(),
                Network: kv.Value.Network ?? "Mainnet"
            ))
            .OrderBy(x => x.Ticker)
            .ToList();
    }

    // =========================
    // GET RATE
    // GET /v1/api/rate?key=...&from=XMR&to=USDT&amount=1&fix=0
    // =========================
    private async Task<ChangeeRateResponse?> GetRateAsync(
        string from, string to, decimal amount, CancellationToken ct)
    {
        var amountStr = amount.ToString("0.########", CultureInfo.InvariantCulture);
        var url = $"/v1/api/rate?key={_opt.ApiKey}&from={from}&to={to}&amount={amountStr}&fix=0";

        var body = await GetAsync(url, ct);
        if (body is null) return null;

        var dto = JsonSerializer.Deserialize<ChangeeRateResponse>(body, ChangeeJsonOpt);

        Console.WriteLine($"[CHANGEE RATE] from={from}, to={to}, amount={amountStr}, result={dto?.Result}, rate={dto?.Rate}");

        return dto;
    }

    // =========================
    // CORE HTTP
    // Auth is a query param (?key=...) — no headers needed
    // =========================
    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        // Log URL with key masked
        Console.WriteLine($"[CHANGEE] GET {url.Replace(_opt.ApiKey, "***")}");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[CHANGEE] Status={res?.Status}, Body={res?.Body}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        return res.Body;
    }

    // =========================
    // HELPERS
    // =========================
    private static string ResolveSymbol(AssetRef asset) =>
        (asset.Ticker ?? "").Trim().ToUpperInvariant();

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(_opt.RequestTimeoutSeconds, 2, 60));

    // =========================
    // DTOs
    // =========================
    private sealed class ChangeeRateResponse
    {
        [JsonPropertyName("result")] public bool? Result { get; set; }
        [JsonPropertyName("rate")] public decimal Rate { get; set; }
        [JsonPropertyName("minamount")] public decimal MinAmount { get; set; }
        [JsonPropertyName("maxamount")] public decimal MaxAmount { get; set; }
        [JsonPropertyName("fix")] public bool? Fix { get; set; }
        [JsonPropertyName("fromNetwork")] public string? FromNetwork { get; set; }
        [JsonPropertyName("toNetwork")] public string? ToNetwork { get; set; }
        [JsonPropertyName("withdrawalFee")] public string? WithdrawalFee { get; set; }
    }

    private sealed class ChangeeCurrencyItem
    {
        [JsonPropertyName("coinName")] public string? CoinName { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("minamount")] public decimal MinAmount { get; set; }
        [JsonPropertyName("maxamount")] public decimal MaxAmount { get; set; }
        [JsonPropertyName("tagname")] public string? TagName { get; set; }
        [JsonPropertyName("available")] public bool Available { get; set; }
    }
}