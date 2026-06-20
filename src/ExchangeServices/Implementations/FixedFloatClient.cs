using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class FixedFloatClient : IFixedFloatClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // FixedFloat returns recv/send as integers (0/1), not booleans.
        Converters = { new FlexibleBoolConverter() }
    };

    private static readonly string[] RateTypes = { "float", "fixed" };

    // Errors that describe the OTHER (to) currency being unavailable. For a BUY
    // the "to" leg is always XMR, so these are identical across every candidate,
    // rate type and direction — short-circuit instead of firing a dozen requests.
    private static readonly string[] ToSideBlockers = { "MAINTENANCE_TO", "OFFLINE_TO", "RESERVE_TO" };

    private readonly HttpClient http;
    private readonly FixedFloatOptions opt;
    private readonly ILogger<FixedFloatClient> log;

    public string ExchangeKey => "fixedfloat";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public FixedFloatClient(HttpClient http, IOptions<FixedFloatOptions> options, ILogger<FixedFloatClient> log)
    {
        this.http = http;
        this.opt = options.Value;
        this.log = log;
    }

    // =========================
    // SELL: 1 XMR → ? quote
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!HasAuth()) return null;

        var attempts = new[] { ("from", 1m) };

        foreach (var fromCcy in CurrencyCodeCandidates(query.Base))
            foreach (var toCcy in CurrencyCodeCandidates(query.Quote))
            {
                var (data, toBlocked) = await TryQuoteAsync("SELL", fromCcy, toCcy, attempts, ct);
                if (toBlocked) return null; // quote currency unavailable — nothing to quote
                if (data is null) continue;

                var fromAmt = data.From?.Amount ?? 0m;
                var toAmt = data.To?.Amount ?? 0m;
                if (fromAmt <= 0 || toAmt <= 0) continue;

                var rate = toAmt / fromAmt; // quote per 1 XMR
                if (rate <= 0) continue;

                return Result(query, rate);
            }

        return null;
    }

    // =========================
    // BUY: ? quote → 1 XMR
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!HasAuth()) return null;

        var probe = query.ProbeAmount ?? 500m;                 // quote units
        var attempts = new[] { ("from", probe), ("to", 1m) };  // "to" amount is XMR

        foreach (var fromCcy in CurrencyCodeCandidates(query.Quote)) // USDTTRC / BTC / ETH
            foreach (var toCcy in CurrencyCodeCandidates(query.Base))   // XMR
            {
                var (data, toBlocked) = await TryQuoteAsync("BUY", fromCcy, toCcy, attempts, ct);
                if (toBlocked) return null; // XMR payout under maintenance/offline — no buy quote exists
                if (data is null) continue;

                var fromAmt = data.From?.Amount ?? 0m; // quote spent
                var toAmt = data.To?.Amount ?? 0m;     // XMR received
                if (fromAmt <= 0 || toAmt <= 0) continue;

                var quotePerXmr = fromAmt / toAmt;
                if (quotePerXmr <= 0) continue;

                return Result(query, quotePerXmr);
            }

        return null;
    }

    private PriceResult Result(PriceQuery query, decimal price) => new(
        Exchange: ExchangeKey,
        Base: query.Base,
        Quote: query.Quote,
        Price: price,
        TimestampUtc: DateTimeOffset.UtcNow,
        CorrelationId: null,
        Raw: null);

    // Returns (data, toBlocked). toBlocked=true means the destination currency is
    // under maintenance/offline/out of reserve — caller should stop entirely.
    private async Task<(PriceDataDto? data, bool toBlocked)> TryQuoteAsync(
        string leg, string fromCcy, string toCcy,
        IEnumerable<(string direction, decimal amount)> attempts, CancellationToken ct)
    {
        foreach (var (direction, amount) in attempts)
            foreach (var type in RateTypes)
            {
                var resp = await PostPriceAsync(type, fromCcy, toCcy, direction, amount, ct);

                if (resp is null)
                {
                    log.LogWarning("[FF] {Leg} {Type}/{Dir} {From}->{To} amt={Amount}: no/unparseable response",
                        leg, type, direction, fromCcy, toCcy, amount);
                    continue;
                }

                if (resp.Code != 0)
                {
                    log.LogWarning("[FF] {Leg} {Type}/{Dir} {From}->{To} amt={Amount}: code={Code} msg={Msg}",
                        leg, type, direction, fromCcy, toCcy, amount, resp.Code, resp.Msg);
                    continue; // e.g. 301 fromCcy incorrect — a bad candidate, keep trying others
                }

                var data = resp.Data;

                if (data?.Errors is { Count: > 0 })
                {
                    if (data.Errors.Any(e => ToSideBlockers.Contains(e, StringComparer.OrdinalIgnoreCase)))
                    {
                        log.LogWarning("[FF] {Leg} {From}->{To}: destination unavailable [{Errors}] — skipping",
                            leg, fromCcy, toCcy, string.Join(",", data.Errors));
                        return (null, true);
                    }

                    if (HasError(data, "LIMIT_MIN"))
                    {
                        var sideMin = direction == "to" ? data.To?.Min : data.From?.Min;
                        if (sideMin is decimal min && min > amount)
                        {
                            var retry = await PostPriceAsync(type, fromCcy, toCcy, direction, min, ct);
                            if (retry?.Code == 0 && IsUsable(retry.Data)) return (retry.Data, false);
                        }
                    }

                    log.LogWarning("[FF] {Leg} {Type}/{Dir} {From}->{To} amt={Amount}: errors=[{Errors}]",
                        leg, type, direction, fromCcy, toCcy, amount, string.Join(",", data.Errors));
                    continue;
                }

                if (!IsUsable(data))
                {
                    log.LogWarning("[FF] {Leg} {Type}/{Dir} {From}->{To} amt={Amount}: empty amounts",
                        leg, type, direction, fromCcy, toCcy, amount);
                    continue;
                }

                return (data, false);
            }

        return (null, false);
    }

    private async Task<PriceResponseDto?> PostPriceAsync(
        string type, string fromCcy, string toCcy, string direction, decimal amount, CancellationToken ct)
    {
        var req = new PriceRequestDto
        {
            Type = type,
            FromCcy = fromCcy,
            ToCcy = toCcy,
            Direction = direction,
            Amount = amount
        };

        return await PostSignedAsync<PriceResponseDto>("/api/v2/price", req, ct);
    }

    private static bool IsUsable(PriceDataDto? data)
        => data is not null
           && data.Errors is not { Count: > 0 }
           && (data.From?.Amount ?? 0m) > 0
           && (data.To?.Amount ?? 0m) > 0;

    private static bool HasError(PriceDataDto data, string code)
        => data.Errors is not null
           && data.Errors.Any(e => string.Equals(e, code, StringComparison.OrdinalIgnoreCase));

    // =========================
    // CURRENCIES: /api/v2/ccies
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (!HasAuth()) return Array.Empty<ExchangeCurrency>();

        var resp = await PostSignedAsync<CciesResponseDto>("/api/v2/ccies", new { }, ct);
        if (resp?.Code != 0 || resp.Data is null || resp.Data.Count == 0)
        {
            log.LogWarning("[FF] ccies code={Code} msg={Msg} count={Count}",
                resp?.Code, resp?.Msg, resp?.Data?.Count);
            return Array.Empty<ExchangeCurrency>();
        }

        var list = new List<ExchangeCurrency>(resp.Data.Count);

        foreach (var c in resp.Data)
        {
            var code = (c.Code ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (c.Recv != true && c.Send != true) continue;

            list.Add(new ExchangeCurrency(
                ExchangeId: code.ToUpperInvariant(),
                Ticker: (c.Coin ?? code).Trim().ToUpperInvariant(),
                Network: NormalizeNetworkForUi(c.Network)
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

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("[FF] {Path} HTTP {Status}: {Body}", path, (int)resp.StatusCode, Trunc(raw));
                return default;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(raw, JsonOpt);
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "[FF] {Path} JSON parse failed: {Body}", path, Trunc(raw));
                return default;
            }
        }
        catch (TaskCanceledException) { return default; }
        catch (OperationCanceledException) { return default; }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "[FF] {Path} HTTP error", path);
            return default;
        }
    }

    private static string Trunc(string? s, int max = 600)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

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
    // FixedFloat's only USDT-Tron code is "USDTTRC". USDTTRC20 / USDTTRX are not
    // valid codes and return code=301 "fromCcy is incorrect".
    // =========================
    private static IEnumerable<string> CurrencyCodeCandidates(AssetRef asset)
    {
        var t = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        var exchId = (asset.ExchangeId ?? "").Trim().ToUpperInvariant();
        var isTron = asset.Network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) ?? false;

        if (t == "USDT" && isTron)
        {
            yield return "USDTTRC";
            if (!string.IsNullOrWhiteSpace(exchId) && exchId != "USDT" && exchId != "USDTTRC")
                yield return exchId;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(exchId))
            yield return exchId;

        if (!string.IsNullOrWhiteSpace(t) && t != exchId)
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
    // Flexible bool: accepts true/false, 0/1, "true"/"false", "0"/"1"
    // =========================
    private sealed class FlexibleBoolConverter : JsonConverter<bool?>
    {
        public override bool? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
            => r.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.Null => null,
                JsonTokenType.Number => r.TryGetInt64(out var n) ? n != 0 : r.GetDouble() != 0,
                JsonTokenType.String => ParseString(r.GetString()),
                _ => null
            };

        private static bool? ParseString(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (bool.TryParse(s, out var b)) return b;
            if (long.TryParse(s, out var n)) return n != 0;
            return null;
        }

        public override void Write(Utf8JsonWriter w, bool? v, JsonSerializerOptions o)
        {
            if (v is null) w.WriteNullValue();
            else w.WriteBooleanValue(v.Value);
        }
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
        [JsonPropertyName("min")] public decimal? Min { get; set; }
        [JsonPropertyName("max")] public decimal? Max { get; set; }
    }
}