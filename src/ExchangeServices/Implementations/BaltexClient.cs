using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class BaltexClient : IBaltexClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private const int DefaultTimeoutSeconds = 8;

    private readonly HttpClient http;
    private readonly BaltexOptions opt;

    public string  ExchangeKey => "baltex";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;

    public BaltexClient(HttpClient http, IOptions<BaltexOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return Array.Empty<ExchangeCurrency>();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/cross-chain/available-currencies");
        AddApiKey(req);

        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var res = await SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
        if (res is null) return Array.Empty<ExchangeCurrency>();
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices)
            return Array.Empty<ExchangeCurrency>();

        var list = JsonSerializer.Deserialize<List<CurrencyDto>>(res.Body, JsonOpt) ?? new();

        return list
            .Where(c => c.Enabled)
            .Select(c =>
            {
                var tickerLower = (c.Ticker ?? "").Trim().ToLowerInvariant();
                var netLower = (c.Network ?? "").Trim().ToLowerInvariant();

                var exchangeId = $"{tickerLower}|{netLower}";
                var tickerUpper = tickerLower.ToUpperInvariant();
                var friendlyNetwork = FromBaltexNetwork(netLower, tickerUpper);

                return new ExchangeCurrency(exchangeId, tickerUpper, friendlyNetwork);
            })
            .ToList();
    }

    // SELL: 1 XMR -> ? USDT
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var (fromCurrency, fromNetwork) = ResolveCurrencyAndNetwork(query.Base);
        var (toCurrency, toNetwork) = ResolveCurrencyAndNetwork(query.Quote);

        if (string.IsNullOrWhiteSpace(fromCurrency) || string.IsNullOrWhiteSpace(fromNetwork) ||
            string.IsNullOrWhiteSpace(toCurrency) || string.IsNullOrWhiteSpace(toNetwork))
            return null;

        var url =
            $"/v1/cross-chain/rate" +
            $"?fromCurrency={Uri.EscapeDataString(fromCurrency)}" +
            $"&fromNetwork={Uri.EscapeDataString(fromNetwork)}" +
            $"&toCurrency={Uri.EscapeDataString(toCurrency)}" +
            $"&toNetwork={Uri.EscapeDataString(toNetwork)}" +
            $"&amount=1" +
            $"&flow=standard";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AddApiKey(req);

        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var res = await SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
        if (res is null) return null;
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices)
            return null;

        RateDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RateDto>(res.Body, JsonOpt);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto?.ToAmount is null || dto.ToAmount <= 0)
            return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: dto.ToAmount.Value,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // BUY: how much USDT needed to receive ~1 XMR (approx by probing then inverting)
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var (fromCurrency, fromNetwork) = ResolveCurrencyAndNetwork(query.Quote); // USDT
        var (toCurrency, toNetwork) = ResolveCurrencyAndNetwork(query.Base);      // XMR

        if (string.IsNullOrWhiteSpace(fromCurrency) || string.IsNullOrWhiteSpace(fromNetwork) ||
            string.IsNullOrWhiteSpace(toCurrency) || string.IsNullOrWhiteSpace(toNetwork))
            return null;

        var testAmounts = new[] { 1m, 10m, 50m, 100m, 250m, 500m };
        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

        foreach (var amt in testAmounts)
        {
            var url =
                $"/v1/cross-chain/rate" +
                $"?fromCurrency={Uri.EscapeDataString(fromCurrency)}" +
                $"&fromNetwork={Uri.EscapeDataString(fromNetwork)}" +
                $"&toCurrency={Uri.EscapeDataString(toCurrency)}" +
                $"&toNetwork={Uri.EscapeDataString(toNetwork)}" +
                $"&amount={Uri.EscapeDataString(amt.ToString(CultureInfo.InvariantCulture))}" +
                $"&flow=standard";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddApiKey(req);

            var res = await SafeHttpExtensions.SendForStringAsync(http, req, timeout, ct);
            if (res is null) continue;
            if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices) continue;

            RateDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<RateDto>(res.Body, JsonOpt);
            }
            catch
            {
                continue;
            }

            if (dto?.ToAmount is null || dto.ToAmount <= 0) continue;

            // amt USDT -> dto.ToAmount XMR
            var usdtPerXmr = amt / dto.ToAmount.Value;
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

    // -----------------------
    // helpers
    // -----------------------

    private void AddApiKey(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("x-api-key", opt.ApiKey);
    }

    private static (string currency, string network) ResolveCurrencyAndNetwork(AssetRef asset)
    {
        // If ExchangeId is "ticker|network"
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId))
        {
            var parts = asset.ExchangeId.Split('|', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                return (parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant());
        }

        var currency = (asset.Ticker ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(currency))
            return ("", "");

        var network = ToBaltexNetwork(asset.Network, currency);
        return (currency, network);
    }

    private static string ToBaltexNetwork(string? friendlyNetwork, string currencyLower)
    {
        if (string.IsNullOrWhiteSpace(friendlyNetwork))
            return currencyLower;

        return friendlyNetwork.Trim() switch
        {
            "Mainnet" => currencyLower,
            "Ethereum" => "eth",
            "Binance Smart Chain" => "bsc",
            "Solana" => "sol",
            "Tron" => "trx",
            _ => friendlyNetwork.Trim().ToLowerInvariant()
        };
    }

    private static string FromBaltexNetwork(string networkLower, string tickerUpper)
    {
        return networkLower switch
        {
            "eth" => "Ethereum",
            "bsc" => "Binance Smart Chain",
            "sol" => "Solana",
            "trx" => "Tron",
            _ when networkLower == tickerUpper.ToLowerInvariant() => "Mainnet",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(networkLower)
        };
    }

    // -----------------------
    // DTOs
    // -----------------------
    private sealed class CurrencyDto
    {
        public string? Ticker { get; set; }
        public string? Network { get; set; }
        public string? Name { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class RateDto
    {
        [JsonPropertyName("min")]
        public decimal? Min { get; set; }

        [JsonPropertyName("max")]
        public decimal? Max { get; set; }

        [JsonPropertyName("fromAmount")]
        public decimal? FromAmount { get; set; }

        [JsonPropertyName("toAmount")]
        public decimal? ToAmount { get; set; }

        [JsonPropertyName("fromCurrency")]
        public string? FromCurrency { get; set; }

        [JsonPropertyName("toCurrency")]
        public string? ToCurrency { get; set; }

        [JsonPropertyName("fromNetwork")]
        public string? FromNetwork { get; set; }

        [JsonPropertyName("toNetwork")]
        public string? ToNetwork { get; set; }

        [JsonPropertyName("amountFromUsd")]
        public string? AmountFromUsd { get; set; }

        [JsonPropertyName("amountToUsd")]
        public string? AmountToUsd { get; set; }
    }
}