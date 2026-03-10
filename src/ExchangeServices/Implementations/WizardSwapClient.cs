using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class WizardSwapClient : IWizardSwapClient
{
    private readonly HttpClient http;
    private readonly WizardSwapOptions opt;

    public string  ExchangeKey => "wizardswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    private readonly object currenciesLock = new();
    private DateTimeOffset currenciesAtUtc;
    private List<ExchangeCurrency>? cachedCurrencies;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public WizardSwapClient(HttpClient http, IOptions<WizardSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // -------------------------
    // Currencies
    // -------------------------

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        lock (currenciesLock)
        {
            if (cachedCurrencies is not null &&
                (DateTimeOffset.UtcNow - currenciesAtUtc).TotalSeconds < Math.Max(10, opt.CurrenciesCacheSeconds))
            {
                return cachedCurrencies;
            }
        }

        var (status, body) = await SendForStringWithRetryAsync(
            requestFactory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "/api/currency");
                AddHeaders(req);
                return req;
            },
            timeout: TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60)),
            retryCount: Math.Clamp(opt.RetryCount, 0, 6),
            ct: ct
        );

        if (status is null || string.IsNullOrWhiteSpace(body)) return Array.Empty<ExchangeCurrency>();
        if ((int)status < 200 || (int)status >= 300) return Array.Empty<ExchangeCurrency>();

        List<ExchangeCurrency> parsed;
        try
        {
            var arr = JsonSerializer.Deserialize<List<CurrencyDto>>(body, JsonOpts) ?? new();
            parsed = arr
                .Where(x => !string.IsNullOrWhiteSpace(x.Symbol))
                .Select(x => new ExchangeCurrency(
                    ExchangeId: x.Symbol!.Trim().ToLowerInvariant(),   // WizardSwap expects lowercase
                    Ticker: x.Symbol!.Trim().ToUpperInvariant(),
                    Network: ""                                       // WizardSwap API doesn't expose networks
                ))
                .OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Network, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            parsed = new List<ExchangeCurrency>();
        }

        lock (currenciesLock)
        {
            cachedCurrencies = parsed;
            currenciesAtUtc = DateTimeOffset.UtcNow;
        }

        return parsed;
    }

    private sealed class CurrencyDto
    {
        public string? Symbol { get; set; }
        public string? Name { get; set; }
        public bool Has_Extra_Id { get; set; }
        public string? Extra_Id { get; set; }
    }

    // -------------------------
    // Pricing
    // -------------------------

    public Task<PriceResult?> GetPriceAsync(PriceQuery query, CancellationToken ct = default)
        => GetSellPriceAsync(query, ct);

    /// <summary>
    /// SELL: QUOTE received for selling 1 BASE
    /// Uses POST /api/estimate with amount_from=1.
    /// </summary>
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = ResolveSymbol(query.Base);
        var to = ResolveSymbol(query.Quote);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;

        var amountTo = await EstimateAsync(from, to, amountFrom: 1m, ct);
        if (amountTo is null || amountTo <= 0m) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: amountTo.Value, // quote per 1 base
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    /// <summary>
    /// BUY: QUOTE required to buy 1 BASE
    /// WizardSwap has only amount_from, so we invert:
    ///   get (BASE per 1 QUOTE) by estimating QUOTE -> BASE with amount_from=1,
    ///   then buyCost = 1 / (BASE per 1 QUOTE).
    /// </summary>
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var baseSym = ResolveSymbol(query.Base);
        var quoteSym = ResolveSymbol(query.Quote);
        if (string.IsNullOrWhiteSpace(baseSym) || string.IsNullOrWhiteSpace(quoteSym)) return null;

        // Estimate: if I send 1 QUOTE, how much BASE do I get?
        var basePerOneQuote = await EstimateAsync(quoteSym, baseSym, amountFrom: 1m, ct);
        if (basePerOneQuote is null || basePerOneQuote <= 0m) return null;

        var cost = 1m / basePerOneQuote.Value; // quote needed for 1 base
        if (cost <= 0m) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: cost,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    private async Task<decimal?> EstimateAsync(string currencyFrom, string currencyTo, decimal amountFrom, CancellationToken ct)
    {
        // Endpoint accepts form-encoded or JSON; we’ll use JSON.
        var payload = new EstimateRequest
        {
            CurrencyFrom = currencyFrom,
            CurrencyTo = currencyTo,
            AmountFrom = amountFrom,
            ApiKey = string.IsNullOrWhiteSpace(opt.ApiKey) ? null : opt.ApiKey
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);

        var (status, body) = await SendForStringWithRetryAsync(
            requestFactory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "/api/estimate");
                AddHeaders(req);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                return req;
            },
            timeout: TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60)),
            retryCount: Math.Clamp(opt.RetryCount, 0, 6),
            ct: ct
        );

        if (status is null || string.IsNullOrWhiteSpace(body)) return null;
        if ((int)status < 200 || (int)status >= 300) return null;

        // Docs show: { "amount_to": Float } or "amount_to" is present.
        // Some implementations may return a raw number; handle both.
        if (TryParseNumberBody(body, out var rawNum))
            return rawNum;

        try
        {
            var dto = JsonSerializer.Deserialize<EstimateResponse>(body, JsonOpts);
            if (dto is null) return null;
            return dto.AmountTo > 0m ? dto.AmountTo : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class EstimateRequest
    {
        [JsonPropertyName("currency_from")]
        public string CurrencyFrom { get; set; } = "";

        [JsonPropertyName("currency_to")]
        public string CurrencyTo { get; set; } = "";

        [JsonPropertyName("amount_from")]
        public decimal AmountFrom { get; set; }

        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }
    }

    private sealed class EstimateResponse
    {
        [JsonPropertyName("amount_to")]
        public decimal AmountTo { get; set; }
    }

    private static bool TryParseNumberBody(string body, out decimal value)
    {
        value = 0m;
        var s = (body ?? "").Trim();

        // raw numeric like: 0.1234
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            value = v;
            return true;
        }

        // or quoted numeric like: "0.1234"
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            var inner = s[1..^1];
            if (decimal.TryParse(inner, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                value = v;
                return true;
            }
        }

        return false;
    }

    private static string ResolveSymbol(AssetRef asset)
    {
        // WizardSwap expects lowercase tickers like "btc", "xmr"
        var ex = (asset.ExchangeId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ex))
        {
            // if you use "USDT|Tron" style internally, just take the left side (WizardSwap doesn’t do networks)
            if (ex.Contains('|'))
            {
                var parts = ex.Split('|', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 1) return parts[0].Trim().ToLowerInvariant();
            }

            return ex.Trim().ToLowerInvariant();
        }

        var t = (asset.Ticker ?? "").Trim();
        return string.IsNullOrWhiteSpace(t) ? "" : t.ToLowerInvariant();
    }

    // -------------------------
    // Headers + sender (new way)
    // -------------------------

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    private async Task<(HttpStatusCode? Status, string? Body)> SendForStringWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        int retryCount,
        CancellationToken ct)
    {
        var attempts = Math.Max(1, retryCount + 1);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var req = requestFactory();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            HttpResponseMessage? resp = null;
            try
            {
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (ShouldRetry(resp.StatusCode) && attempt < attempts - 1)
                {
                    resp.Dispose();
                    await BackoffAsync(attempt, ct);
                    continue;
                }

                return (resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                resp?.Dispose();
                if (attempt < attempts - 1)
                {
                    await BackoffAsync(attempt, ct);
                    continue;
                }
                return (null, null);
            }
            catch (HttpRequestException)
            {
                resp?.Dispose();
                if (attempt < attempts - 1)
                {
                    await BackoffAsync(attempt, ct);
                    continue;
                }
                return (null, null);
            }
            finally
            {
                resp?.Dispose();
            }
        }

        return (null, null);
    }

    private static bool ShouldRetry(HttpStatusCode code)
    {
        var n = (int)code;
        if (code == HttpStatusCode.RequestTimeout) return true; // 408
        if (n == 429) return true;                              // Too Many Requests
        if (n >= 500 && n <= 599) return true;                  // 5xx
        return false;
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = Math.Min(2000, (int)(200 * Math.Pow(2, attempt)));
        ms += Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }
}