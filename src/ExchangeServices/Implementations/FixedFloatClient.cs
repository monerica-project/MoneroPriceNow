using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class FixedFloatClient : IFixedFloatClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient http;
    private readonly FixedFloatOptions opt;

    public string ExchangeKey => "fixedfloat";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    public FixedFloatClient(HttpClient http, IOptions<FixedFloatOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR → ? USDT
    // direction="from", amount=1 XMR — matches exactly what the website quotes
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!HasAuth()) return null;

        foreach (var fromCcy in CurrencyCodeCandidates(query.Base))
            foreach (var toCcy in CurrencyCodeCandidates(query.Quote))
            {
                var req = new PriceRequestDto
                {
                    Type = "float",
                    FromCcy = fromCcy,
                    ToCcy = toCcy,
                    Direction = "from",
                    Amount = 1m          // send 1 XMR
                };

                var resp = await PostSignedAsync<PriceResponseDto>("/api/v2/price", req, ct);
                if (resp?.Code != 0) continue;

                var data = resp.Data;
                if (data?.Errors is { Count: > 0 }) continue;

                // to.amount / from.amount = USDT per XMR (normalises rounding)
                var fromAmt = data?.From?.Amount ?? 0m;
                var toAmt = data?.To?.Amount ?? 0m;
                if (fromAmt <= 0 || toAmt <= 0) continue;

                var rate = toAmt / fromAmt;
                if (rate <= 0) continue;

                return new PriceResult(
                    Exchange: ExchangeKey,
                    Base: query.Base,
                    Quote: query.Quote,
                    Price: rate,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    CorrelationId: null,
                    Raw: null
                );
            }

        return null;
    }

    // =========================
    // BUY: ? USDT → 1 XMR
    // Probe with direction="from", amount=500 USDT → XMR
    // Rate = 500 / to.amount = USDT per 1 XMR
    // direction="to" amount=1 is distorted by minimum trade size (~0.998 XMR returned)
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!HasAuth()) return null;

        const decimal probeUsdt = 500m;

        foreach (var fromCcy in CurrencyCodeCandidates(query.Quote)) // USDTTRC
            foreach (var toCcy in CurrencyCodeCandidates(query.Base))   // XMR
            {
                var req = new PriceRequestDto
                {
                    Type = "float",
                    FromCcy = fromCcy,
                    ToCcy = toCcy,
                    Direction = "from",    // ← send 500 USDT, read XMR received
                    Amount = probeUsdt
                };

                var resp = await PostSignedAsync<PriceResponseDto>("/api/v2/price", req, ct);
                if (resp?.Code != 0) continue;

                var data = resp.Data;
                if (data?.Errors is { Count: > 0 }) continue;

                // from.amount = USDT actually used (≈500)
                // to.amount   = XMR received for that USDT
                var fromAmt = data?.From?.Amount ?? 0m;
                var toAmt = data?.To?.Amount ?? 0m;
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
    // CURRENCIES: /api/v2/ccies
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (!HasAuth()) return Array.Empty<ExchangeCurrency>();

        var resp = await PostSignedAsync<CciesResponseDto>("/api/v2/ccies", new { }, ct);
        if (resp?.Code != 0 || resp.Data is null || resp.Data.Count == 0)
            return Array.Empty<ExchangeCurrency>();

        var list = new List<ExchangeCurrency>(resp.Data.Count);

        foreach (var c in resp.Data)
        {
            var code = (c.Code ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            if (c.Recv != true && c.Send != true) continue;

            var ticker = (c.Coin ?? code).Trim().ToUpperInvariant();
            var network = NormalizeNetworkForUi(c.Network);

            list.Add(new ExchangeCurrency(
                ExchangeId: code.ToUpperInvariant(),
                Ticker: ticker,
                Network: network
            ));
        }

        return list
            .DistinctBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Ticker)
            .ThenBy(x => x.Network)
            .ToList();
    }

    // =========================
    // INTERNAL: signed POST
    // =========================
    private async Task<T?> PostSignedAsync<T>(string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, JsonOpt);
        var sign = ComputeHmacSha256Hex(opt.ApiSecret, json);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("X-API-SIGN", sign);

            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Content.Headers.ContentType!.CharSet = "UTF-8";

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode) return default;

            return JsonSerializer.Deserialize<T>(raw, JsonOpt);
        }
        catch (TaskCanceledException) { return default; }
        catch (OperationCanceledException) { return default; }
        catch (HttpRequestException) { return default; }
        catch (JsonException) { return default; }
    }

    private bool HasAuth()
        => !string.IsNullOrWhiteSpace(opt.ApiKey) && !string.IsNullOrWhiteSpace(opt.ApiSecret);

    private static string ComputeHmacSha256Hex(string secret, string payload)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var msg = Encoding.UTF8.GetBytes(payload);
        using var h = new HMACSHA256(key);
        return Convert.ToHexString(h.ComputeHash(msg)).ToLowerInvariant();
    }

    // =========================
    // Currency code candidates
    // =========================
    private static IEnumerable<string> CurrencyCodeCandidates(AssetRef asset)
    {
        var t = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        var exchId = (asset.ExchangeId ?? "").Trim().ToUpperInvariant();
        var isTron = asset.Network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) ?? false;

        // For USDT/Tron, always lead with the explicit Tron codes so FixedFloat
        // matches the right currency regardless of what ExchangeId was resolved to.
        if (t == "USDT" && isTron)
        {
            yield return "USDTTRC";
            yield return "USDTTRC20";
            yield return "USDTTRX";
            // also yield ExchangeId in case none of the above match
            if (!string.IsNullOrWhiteSpace(exchId) && exchId != "USDT")
                yield return exchId;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(exchId))
            yield return exchId;

        if (!string.IsNullOrWhiteSpace(t))
            yield return t;
    }

    private static string NormalizeNetworkForUi(string? ffNetwork)
    {
        if (string.IsNullOrWhiteSpace(ffNetwork)) return "Mainnet";

        var n = ffNetwork.Trim().ToUpperInvariant();

        return n switch
        {
            "TRX" or "TRC20" or "TRON" => "Tron",
            "ETH" or "ERC20" => "Ethereum",
            "BSC" => "Binance Smart Chain",
            "SOL" => "Solana",
            "ARBITRUM" => "Arbitrum",
            "BASE" => "Base",
            "MATIC" or "POLYGON" => "Polygon",
            "BTC" or "XMR" or "LTC"
                or "DOGE" or "DASH"
                or "ZEC" => "Mainnet",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n.ToLowerInvariant())
        };
    }

    // =========================
    // DTOs
    // =========================
    private sealed class CciesResponseDto
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("data")] public List<CcyDto>? Data { get; set; }
    }

    private sealed class CcyDto
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("coin")] public string? Coin { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("recv")] public bool? Recv { get; set; }
        [JsonPropertyName("send")] public bool? Send { get; set; }
    }

    private sealed class PriceRequestDto
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "float";
        [JsonPropertyName("fromCcy")] public string FromCcy { get; set; } = "";
        [JsonPropertyName("toCcy")] public string ToCcy { get; set; } = "";
        [JsonPropertyName("direction")] public string Direction { get; set; } = "from";
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class PriceResponseDto
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("data")] public PriceDataDto? Data { get; set; }
    }

    private sealed class PriceDataDto
    {
        [JsonPropertyName("from")] public SideDto? From { get; set; }
        [JsonPropertyName("to")] public SideDto? To { get; set; }
        [JsonPropertyName("errors")] public List<string>? Errors { get; set; }
    }

    private sealed class SideDto
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("coin")] public string? Coin { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("min")] public decimal Min { get; set; }
        [JsonPropertyName("max")] public decimal Max { get; set; }
    }
}