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

    private readonly HttpClient     _http;
    private readonly ChangeeOptions _opt;

    public string  ExchangeKey => "changee";
    public string  SiteName    => _opt.SiteName;
    public string? SiteUrl     => _opt.SiteUrl;

    public ChangeeClient(HttpClient http, IOptions<ChangeeOptions> options)
    {
        _http = http;
        _opt  = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> ? USDT
    // GET /v1/api/rate?key=...&from=XMR&to=USDT&amount=1
    // Returns rate field = USDT per XMR
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = ResolveSymbol(query.Base);
        var to   = ResolveSymbol(query.Quote);

        Console.WriteLine($"[CHANGEE SELL] from={from}, to={to}");

        var url = $"/v1/api/rate?key={_opt.ApiKey}&from={from}&to={to}&amount=1&fix=0";

        var res = await GetAsync(url, ct);
        if (res is null) return null;

        var dto = JsonSerializer.Deserialize<ChangeeRateResponse>(res, ChangeeJsonOpt);

        Console.WriteLine($"[CHANGEE SELL] result={dto?.Result}, rate={dto?.Rate}");

        if (dto is null || dto.Result != true || dto.Rate <= 0) return null;

        return new PriceResult(
            Exchange:      ExchangeKey,
            Base:          query.Base,
            Quote:         query.Quote,
            Price:         dto.Rate,
            TimestampUtc:  DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw:           null
        );
    }

    // =========================
    // BUY: ? USDT needed to receive 1 XMR
    // GET /v1/api/payment/rate?key=...&from=USDT&to=XMR&amountTo=1
    // Returns rate = USDT per XMR (i.e. how much USDT to send to receive 1 XMR)
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = ResolveSymbol(query.Quote); // USDT
        var to   = ResolveSymbol(query.Base);  // XMR

        Console.WriteLine($"[CHANGEE BUY] from={from}, to={to}");

        // amountTo=1 means "I want to receive 1 XMR, how much USDT do I need to send?"
        var url = $"/v1/api/payment/rate?key={_opt.ApiKey}&from={from}&to={to}&amountTo=1";

        var res = await GetAsync(url, ct);
        if (res is null) return null;

        var dto = JsonSerializer.Deserialize<ChangeePaymentRateResponse>(res, ChangeeJsonOpt);

        Console.WriteLine($"[CHANGEE BUY] result={dto?.Result}, rate={dto?.Rate}");

        if (dto is null || dto.Result != true || dto.Rate <= 0) return null;

        // rate here = from/to exchange rate i.e. how much USDT per 1 XMR
        // The payment/rate endpoint returns rate as the inverse (XMR per USDT),
        // so we invert it to get USDT per XMR
        var usdtPerXmr = dto.Rate > 0 ? 1m / dto.Rate : 0m;

        Console.WriteLine($"[CHANGEE BUY] rawRate={dto.Rate}, usdtPerXmr={usdtPerXmr}");

        if (usdtPerXmr <= 0) return null;

        return new PriceResult(
            Exchange:      ExchangeKey,
            Base:          query.Base,
            Quote:         query.Quote,
            Price:         usdtPerXmr,
            TimestampUtc:  DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw:           null
        );
    }

    // =========================
    // CURRENCIES
    // GET /v1/api/currencies?key=...
    // Returns a dictionary keyed by ticker symbol
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var url = $"/v1/api/currencies?key={_opt.ApiKey}";
        var res = await GetAsync(url, ct);

        Console.WriteLine($"[CHANGEE CURRENCIES] Body={res?[..Math.Min(300, res?.Length ?? 0)]}");

        if (res is null) return Array.Empty<ExchangeCurrency>();

        // Response is a dictionary: { "XMR": { coinName, network, ... }, "BTC": { ... } }
        var dto = JsonSerializer.Deserialize<Dictionary<string, ChangeeCurrencyItem>>(res, ChangeeJsonOpt);
        if (dto is null || dto.Count == 0) return Array.Empty<ExchangeCurrency>();

        return dto
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value.Available)
            .Select(kv => new ExchangeCurrency(
                ExchangeId: kv.Key.Trim().ToUpperInvariant(),
                Ticker:     kv.Key.Trim().ToUpperInvariant(),
                Network:    kv.Value.Network ?? "Mainnet"
            ))
            .OrderBy(x => x.Ticker)
            .ToList();
    }

    // =========================
    // CORE HTTP — all Changee calls are GET with key in query string
    // No auth headers needed — key is a query param
    // =========================
    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
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
    private static string ResolveSymbol(AssetRef asset)
    {
        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();

        // Changee uses plain ticker symbols with no network suffix
        // USDT on Changee is just "USDT" (they determine network internally)
        return ticker;
    }

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(_opt.RequestTimeoutSeconds, 2, 60));

    // =========================
    // DTOs
    // =========================
    private sealed class ChangeeRateResponse
    {
        // /v1/api/rate response
        [JsonPropertyName("result")]       public bool?   Result       { get; set; }
        [JsonPropertyName("rate")]         public decimal Rate         { get; set; }
        [JsonPropertyName("minamount")]    public decimal MinAmount    { get; set; }
        [JsonPropertyName("maxamount")]    public decimal MaxAmount    { get; set; }
        [JsonPropertyName("fix")]          public bool?   Fix          { get; set; }
        [JsonPropertyName("fromNetwork")]  public string? FromNetwork  { get; set; }
        [JsonPropertyName("toNetwork")]    public string? ToNetwork    { get; set; }
        [JsonPropertyName("withdrawalFee")]public string? WithdrawalFee{ get; set; }
    }

    private sealed class ChangeePaymentRateResponse
    {
        // /v1/api/payment/rate response
        [JsonPropertyName("result")]    public bool?   Result    { get; set; }
        [JsonPropertyName("rate")]      public decimal Rate      { get; set; }
        [JsonPropertyName("minamount")] public decimal MinAmount { get; set; }
        [JsonPropertyName("maxamount")] public decimal MaxAmount { get; set; }
    }

    private sealed class ChangeeCurrencyItem
    {
        [JsonPropertyName("coinName")]  public string? CoinName  { get; set; }
        [JsonPropertyName("network")]   public string? Network   { get; set; }
        [JsonPropertyName("minamount")] public decimal MinAmount { get; set; }
        [JsonPropertyName("maxamount")] public decimal MaxAmount { get; set; }
        [JsonPropertyName("tagname")]   public string? TagName   { get; set; }
        [JsonPropertyName("available")] public bool    Available { get; set; }
    }
}
