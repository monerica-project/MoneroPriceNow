using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class OctoSwapClient : IOctoSwapClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int DefaultTimeoutSeconds = 8;

    private readonly HttpClient http;
    private readonly OctoSwapOptions opt;

    public string  ExchangeKey => "octoswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    public OctoSwapClient(HttpClient http, IOptions<OctoSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // SELL: 1 XMR -> how much USDT(TRX) you receive
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var fromSymbol = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToUpperInvariant();
        var toSymbol = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToUpperInvariant();

        var fromNetwork = ToOctoNetwork(fromSymbol, query.Base.Network);
        var toNetwork = ToOctoNetwork(toSymbol, query.Quote.Network);

        var url =
            $"/api/price" +
            $"?amount=1" +
            $"&type=variable" +
            $"&from={Uri.EscapeDataString(fromSymbol)}" +
            $"&to={Uri.EscapeDataString(toSymbol)}";

        if (!string.IsNullOrWhiteSpace(fromNetwork))
            url += $"&from_network={Uri.EscapeDataString(fromNetwork)}";

        if (!string.IsNullOrWhiteSpace(toNetwork))
            url += $"&to_network={Uri.EscapeDataString(toNetwork)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);

        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var res = await SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
        if (res is null) return null;
        if ((int)res.Status < 200 || (int)res.Status >= 300) return null;

        PriceDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PriceDto>(res.Body, JsonOpt);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null || dto.Price <= 0)
            return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: (decimal)dto.Price,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // BUY: how much USDT(TRX) is needed to get 1 XMR
    // We call reverse: 1 USDT(TRX) -> XMR, then invert.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var usdtSymbol = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToUpperInvariant();
        var xmrSymbol = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToUpperInvariant();

        var usdtNetwork = ToOctoNetwork(usdtSymbol, query.Quote.Network);
        var xmrNetwork = ToOctoNetwork(xmrSymbol, query.Base.Network);

        var url =
            $"/api/price" +
            $"?amount=1" +
            $"&type=variable" +
            $"&from={Uri.EscapeDataString(usdtSymbol)}" +
            $"&to={Uri.EscapeDataString(xmrSymbol)}";

        if (!string.IsNullOrWhiteSpace(usdtNetwork))
            url += $"&from_network={Uri.EscapeDataString(usdtNetwork)}";

        if (!string.IsNullOrWhiteSpace(xmrNetwork))
            url += $"&to_network={Uri.EscapeDataString(xmrNetwork)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddAuth(req);

        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var res = await SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
        if (res is null) return null;
        if ((int)res.Status < 200 || (int)res.Status >= 300) return null;

        PriceDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PriceDto>(res.Body, JsonOpt);
        }
        catch (JsonException)
        {
            return null;
        }

        // dto.Price is "to per 1 from" => XMR per 1 USDT
        if (dto is null || dto.Price <= 0)
            return null;

        var xmrPer1Usdt = (decimal)dto.Price;
        var usdtPer1Xmr = 1m / xmrPer1Usdt;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: usdtPer1Xmr,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("api-key", opt.ApiKey);

        if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    private static string? ToOctoNetwork(string ticker, string? network)
    {
        if (string.IsNullOrWhiteSpace(network))
            return null;

        return network.Trim() switch
        {
            "Mainnet" => null,

            "Tron" => "TRX",
            "Ethereum" => "ETH",
            "Binance Smart Chain" => "BSC",
            "Solana" => "SOL",

            _ => network.Trim()
        };
    }

    private sealed class PriceDto
    {
        [JsonPropertyName("price")]
        public double Price { get; set; }

        [JsonPropertyName("min_deposit")]
        public double MinDeposit { get; set; }

        [JsonPropertyName("max_deposit")]
        public double MaxDeposit { get; set; }
    }
}