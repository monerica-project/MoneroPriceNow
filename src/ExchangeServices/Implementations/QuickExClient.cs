using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class QuickExClient : IQuickExClient
{
    private readonly HttpClient http;
    private readonly QuickExOptions opt;

    public string ExchangeKey => "quickex";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;

    private readonly object instrumentsLock = new();
    private DateTimeOffset instrumentsAtUtc;
    private IReadOnlyList<InstrumentDto>? cachedInstruments;

    private readonly object quoteLock = new();
    private readonly Dictionary<string, (DateTimeOffset at, decimal rate)> quoteCache
        = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private const decimal BuyProbeUsdt = 100m;

    public QuickExClient(HttpClient http, IOptions<QuickExOptions> options)
    {
        this.http = http;
        this.opt = options.Value ?? new QuickExOptions();

        if (this.http.BaseAddress is null && !string.IsNullOrWhiteSpace(this.opt.BaseUrl))
            this.http.BaseAddress = new Uri(this.opt.BaseUrl!, UriKind.Absolute);
    }

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var instruments = await GetInstrumentsAsync(ct);
        if (instruments.Count == 0) return Array.Empty<ExchangeCurrency>();

        return instruments
            .Select(i => new ExchangeCurrency(
                ExchangeId: BuildExchangeId(i),
                Ticker: i.CurrencyTitle?.Trim().ToUpperInvariant() ?? "",
                Network: i.NetworkTitle?.Trim() ?? ""))
            .Where(c => !string.IsNullOrWhiteSpace(c.Ticker))
            .OrderBy(c => c.Ticker)
            .ToList();
    }

    public Task<PriceResult?> GetPriceAsync(PriceQuery query, CancellationToken ct = default)
        => GetSellPriceAsync(query, ct);

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var instruments = await GetInstrumentsAsync(ct);
        if (instruments.Count == 0) return null;

        var fromCands = MatchInstruments(query.Base, instruments);
        var toCands = MatchInstruments(query.Quote, instruments);
        if (fromCands.Count == 0 || toCands.Count == 0) return null;

        var rateMode = NormalizeRateMode(opt.RateType);

        foreach (var from in fromCands)
            foreach (var to in toCands)
            {
                var rate = await GetRateAsync(from, to, amount: 1m,
                    amountSide: from, rateMode, ct);

                if (rate is not null && rate.Value > 0m)
                    return new PriceResult(
                        Exchange: ExchangeKey,
                        Base: query.Base,
                        Quote: query.Quote,
                        Price: rate.Value,
                        TimestampUtc: DateTimeOffset.UtcNow,
                        CorrelationId: null,
                        Raw: null);
            }

        return null;
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var instruments = await GetInstrumentsAsync(ct);
        if (instruments.Count == 0) return null;

        var baseCands = MatchInstruments(query.Base, instruments);
        var quoteCands = MatchInstruments(query.Quote, instruments);
        if (baseCands.Count == 0 || quoteCands.Count == 0) return null;

        var rateMode = NormalizeRateMode(opt.RateType);

        foreach (var quoteInst in quoteCands)
            foreach (var baseInst in baseCands)
            {
                var xmrReceived = await GetRateAsync(
                    quoteInst, baseInst,
                    amount: BuyProbeUsdt,
                    amountSide: quoteInst,
                    rateMode,
                    ct);

                if (xmrReceived is not null && xmrReceived.Value > 0m)
                {
                    var buyPrice = BuyProbeUsdt / xmrReceived.Value;
                    if (buyPrice > 0m)
                        return new PriceResult(
                            Exchange: ExchangeKey,
                            Base: query.Base,
                            Quote: query.Quote,
                            Price: buyPrice,
                            TimestampUtc: DateTimeOffset.UtcNow,
                            CorrelationId: null,
                            Raw: null);
                }
            }

        return null;
    }

    private async Task<decimal?> GetRateAsync(
        InstrumentDto from,
        InstrumentDto to,
        decimal amount,
        InstrumentDto amountSide,
        string rateMode,
        CancellationToken ct)
    {
        var key = $"{rateMode}|{BuildExchangeId(from)}->{BuildExchangeId(to)}@{amount}";

        lock (quoteLock)
        {
            if (quoteCache.TryGetValue(key, out var hit) &&
                (DateTimeOffset.UtcNow - hit.at).TotalSeconds < Math.Max(1, opt.QuoteCacheSeconds))
                return hit.rate;
        }

        var result = await FetchRateDirectAsync(from, to, amount, amountSide, rateMode, ct);

        if (result is not null && result.Value > 0m)
            lock (quoteLock) quoteCache[key] = (DateTimeOffset.UtcNow, result.Value);

        return result;
    }

    private async Task<decimal?> FetchRateDirectAsync(
        InstrumentDto from,
        InstrumentDto to,
        decimal amount,
        InstrumentDto amountSide,
        string rateMode,
        CancellationToken ct)
    {
        // Always use v2 — v1 is blocked by Cloudflare
        var body = await FetchV2Async(from, to, amount, amountSide, rateMode, ct);
        if (string.IsNullOrWhiteSpace(body)) return null;
        return ParseAmountToGet(body, amount, amountSide, to);
    }

    // v2: POST /api/v2/rate with HMAC-SHA256 signing
    private async Task<string?> FetchV2Async(
        InstrumentDto from,
        InstrumentDto to,
        decimal amount,
        InstrumentDto amountSide,
        string rateMode,
        CancellationToken ct)
    {
        var bodyObj = new
        {
            instrumentFromCurrencyTitle = from.CurrencyTitle,
            instrumentFromNetworkTitle = from.NetworkTitle,
            instrumentToCurrencyTitle = to.CurrencyTitle,
            instrumentToNetworkTitle = to.NetworkTitle,
            claimedDepositAmount = amount.ToString("G", CultureInfo.InvariantCulture),
            claimedDepositAmountCurrency = amountSide.CurrencyTitle,
            rateMode = rateMode,
            markup = "0"
        };
        var jsonBody = JsonSerializer.Serialize(bodyObj);

        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var signature = BuildSignature(opt.ApiPrivateKey!, nonce, jsonBody, opt.ApiPublicKey!);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/rate")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        AddCommonHeaders(req);
        // Header names exactly as per Quickex v2 docs
        req.Headers.TryAddWithoutValidation("X-Api-Public-Key", opt.ApiPublicKey!);
        req.Headers.TryAddWithoutValidation("X-Api-Timestamp", nonce);
        req.Headers.TryAddWithoutValidation("X-Api-Signature", signature);

        var (_, body) = await SendWithRetryAsync(req, ct);
        return body;
    }

    private static string BuildSignature(string secret, string nonce, string jsonBody, string publicKey)
    {
        // StrToSign = timestamp + body + publicKey  (per Quickex v2 docs)
        // Signature = Base64( HMAC_SHA256(StrToSign, secretKey) )
        var strToSign = Encoding.UTF8.GetBytes(nonce + jsonBody + publicKey);
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, strToSign);
        return Convert.ToBase64String(hash);  // Base64, NOT hex
    }

    private async Task<IReadOnlyList<InstrumentDto>> GetInstrumentsAsync(CancellationToken ct)
    {
        lock (instrumentsLock)
        {
            if (cachedInstruments is not null &&
                (DateTimeOffset.UtcNow - instrumentsAtUtc).TotalSeconds
                    < Math.Max(10, opt.PairsCacheSeconds))
                return cachedInstruments;
        }

        var list = await FetchInstrumentsAsync(ct);

        lock (instrumentsLock)
        {
            cachedInstruments = list;
            instrumentsAtUtc = DateTimeOffset.UtcNow;
        }
        return list;
    }

    private async Task<IReadOnlyList<InstrumentDto>> FetchInstrumentsAsync(CancellationToken ct)
    {
        // v2 instruments endpoint — v1 is Cloudflare-blocked
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v2/instruments");
        AddCommonHeaders(req);

        // v2 instruments requires API key header even though it's a GET
        if (!string.IsNullOrWhiteSpace(opt.ApiPublicKey))
            req.Headers.TryAddWithoutValidation("X-Api-Public-Key", opt.ApiPublicKey);

        var (_, body) = await SendWithRetryAsync(req, ct);
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<InstrumentDto>();

        return ParseInstruments(body);
    }

    private static decimal? ParseAmountToGet(
        string json,
        decimal probeAmount,
        InstrumentDto amountSide,
        InstrumentDto toInstrument)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var amountToGet = GetDecimal(root, "amountToGet")
                           ?? GetDecimal(root, "amount_to_get")
                           ?? GetDecimal(root, "amountOut")
                           ?? GetDecimal(root, "result");

            if (amountToGet is not null && amountToGet.Value > 0m)
                return probeAmount != 1m ? amountToGet.Value / probeAmount : amountToGet.Value;

            var price = GetDecimal(root, "price")
                     ?? GetDecimal(root, "rate")
                     ?? GetDecimal(root, "exchange_rate");

            return price is not null && price.Value > 0m ? price.Value : null;
        }
        catch { return null; }
    }

    private static IReadOnlyList<InstrumentDto> ParseInstruments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<InstrumentDto>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array)
                arr = root;
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                arr = d;
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("instruments", out var ins) && ins.ValueKind == JsonValueKind.Array)
                arr = ins;
            else
                return Array.Empty<InstrumentDto>();

            var list = new List<InstrumentDto>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var currency = GetString(el, "currencyTitle") ?? GetString(el, "currency") ?? "";
                var network = GetString(el, "networkTitle") ?? GetString(el, "network") ?? "";
                if (string.IsNullOrWhiteSpace(currency)) continue;
                list.Add(new InstrumentDto(currency.Trim(), network.Trim()));
            }
            return list;
        }
        catch { return Array.Empty<InstrumentDto>(); }
    }

    private static List<InstrumentDto> MatchInstruments(
        AssetRef asset,
        IReadOnlyList<InstrumentDto> instruments)
    {
        var results = new List<InstrumentDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfNew(InstrumentDto d)
        {
            var k = BuildExchangeId(d);
            if (seen.Add(k)) results.Add(d);
        }

        var exId = (asset.ExchangeId ?? "").Trim();
        if (exId.Contains('|'))
        {
            var parts = exId.Split('|', 2,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                var ticker = parts[0].ToUpperInvariant();
                var network = parts[1];
                foreach (var suf in NetworkSuffixCandidates(network))
                    foreach (var inst in instruments.Where(i =>
                        i.CurrencyTitle.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
                        i.NetworkTitle.Contains(suf, StringComparison.OrdinalIgnoreCase)))
                        AddIfNew(inst);
            }
        }

        if (!string.IsNullOrWhiteSpace(exId) && !exId.Contains('|'))
        {
            foreach (var inst in instruments.Where(i =>
                i.CurrencyTitle.Equals(exId, StringComparison.OrdinalIgnoreCase)))
                AddIfNew(inst);
        }

        var ticker2 = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(ticker2))
        {
            foreach (var suf in NetworkSuffixCandidates(asset.Network))
                foreach (var inst in instruments.Where(i =>
                    i.CurrencyTitle.Equals(ticker2, StringComparison.OrdinalIgnoreCase) &&
                    i.NetworkTitle.Contains(suf, StringComparison.OrdinalIgnoreCase)))
                    AddIfNew(inst);

            foreach (var inst in instruments
                .Where(i => i.CurrencyTitle.Equals(ticker2, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => NetworkPriority(i.NetworkTitle)))
                AddIfNew(inst);
        }

        return results;
    }

    private static IEnumerable<string> NetworkSuffixCandidates(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return Array.Empty<string>();

        return network.Trim() switch
        {
            "Tron" or "TRX" or "TRC20" => new[] { "TRC20", "TRC", "TRX", "Tron" },
            "Ethereum" or "ETH" or "ERC20" => new[] { "ERC20", "ETH", "Ethereum" },
            "Binance Smart Chain" or "BSC" or "BEP20" => new[] { "BEP20", "BSC" },
            "Solana" or "SOL" => new[] { "SOL", "Solana" },
            "mainnet" => new[] { "mainnet", "" },
            var n => new[] { n }
        };
    }

    private static int NetworkPriority(string net) => net.ToUpperInvariant() switch
    {
        "MAINNET" => 0,
        "TRC20" => 1,
        "ERC20" => 2,
        "BEP20" => 3,
        _ => 99
    };

    private static string BuildExchangeId(InstrumentDto d)
    {
        var t = d.CurrencyTitle.Trim().ToUpperInvariant();
        var n = d.NetworkTitle.Trim();
        return string.IsNullOrWhiteSpace(n) ? t : $"{t}|{n}";
    }

    private static string NormalizeRateMode(string? s) =>
        (s ?? "").Trim().Equals("FIXED", StringComparison.OrdinalIgnoreCase) ? "FIXED" : "FLOATING";

    private void AddCommonHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    private async Task<(HttpStatusCode? Status, string? Body)> SendWithRetryAsync(
        HttpRequestMessage req, CancellationToken ct)
    {
        var method = req.Method;
        var url = req.RequestUri?.ToString() ?? "";
        var content = req.Content is null ? null
            : await req.Content.ReadAsStringAsync(ct);
        var headers = req.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToList());

        HttpRequestMessage Build()
        {
            var r = new HttpRequestMessage(method, url);
            foreach (var (k, v) in headers)
                r.Headers.TryAddWithoutValidation(k, v);
            if (content is not null)
                r.Content = new StringContent(content, Encoding.UTF8, "application/json");
            return r;
        }

        var attempts = Math.Max(1, opt.RetryCount + 1);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var r = Build();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                using var resp = await http
                    .SendAsync(r, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(false);

                var body = await resp.Content
                    .ReadAsStringAsync(cts.Token)
                    .ConfigureAwait(false);

                if (ShouldRetry(resp.StatusCode) && i < attempts - 1)
                {
                    await BackoffAsync(i, ct).ConfigureAwait(false);
                    continue;
                }
                return (resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (i < attempts - 1) { await BackoffAsync(i, ct).ConfigureAwait(false); continue; }
                return (null, null);
            }
            catch (HttpRequestException)
            {
                if (i < attempts - 1) { await BackoffAsync(i, ct).ConfigureAwait(false); continue; }
                return (null, null);
            }
        }
        return (null, null);
    }

    private static bool ShouldRetry(HttpStatusCode code)
    {
        var n = (int)code;
        return code == HttpStatusCode.RequestTimeout || n == 429 || (n >= 500 && n <= 599);
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = Math.Min(2000, (int)(200 * Math.Pow(2, attempt))) + Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }

    private static string? GetString(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
    }

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(
                p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null
        };
    }

    private sealed record InstrumentDto(string CurrencyTitle, string NetworkTitle);
}