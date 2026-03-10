using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class ChangeNowClient : IChangeNowClient
{
    private readonly HttpClient http;
    private readonly ChangeNowOptions opt;

    public string  ExchangeKey => "changenow";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    // Currencies cache
    private readonly object currenciesLock = new();
    private DateTimeOffset currenciesAtUtc;
    private List<ExchangeCurrency>? cachedCurrencies;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public ChangeNowClient(HttpClient http, IOptions<ChangeNowOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // -------------------------
    // Public API
    // -------------------------

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        // cache
        lock (currenciesLock)
        {
            if (cachedCurrencies is not null &&
                (DateTimeOffset.UtcNow - currenciesAtUtc).TotalSeconds < Math.Max(10, opt.CurrenciesCacheSeconds))
            {
                return cachedCurrencies;
            }
        }

        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return Array.Empty<ExchangeCurrency>();

        // Depending on partner access, this may or may not require auth.
        var url = "/v2/exchange/currencies?active=true";

        var (status, body) = await SendForStringWithRetryAsync(
            requestFactory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
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
            parsed = ParseCurrencies(body);
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

    // SELL = how much QUOTE you receive for 1 BASE
    public Task<PriceResult?> GetPriceAsync(PriceQuery query, CancellationToken ct = default)
        => GetSellPriceAsync(query, ct);

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var (coinFromRaw, netFromRaw) = ResolveCoinAndNetwork(query.Base);
        var (coinToRaw, netToRaw) = ResolveCoinAndNetwork(query.Quote);

        if (string.IsNullOrWhiteSpace(coinFromRaw) || string.IsNullOrWhiteSpace(coinToRaw))
            return null;

        // Try a small set of robust variants to avoid "0/null" from bad params/codes.
        var flows = BuildFlowAttempts(opt.Flow);

        // NOTE: ChangeNOW quotes can be amount-dependent. We normalize to "per 1 BASE" anyway.
        const decimal fromAmountUsed = 1m;

        foreach (var flow in flows)
        {
            foreach (var fromCandidate in ExpandCurrencyCandidates(coinFromRaw, netFromRaw))
            {
                foreach (var toCandidate in ExpandCurrencyCandidates(coinToRaw, netToRaw))
                {
                    // direct: set fromAmount=1, read toAmount
                    var dto = await CallEstimatedAmountAsync(
                        coinFrom: fromCandidate.coin,
                        networkFrom: fromCandidate.network,
                        coinTo: toCandidate.coin,
                        networkTo: toCandidate.network,
                        flow: flow,
                        type: "direct",
                        fromAmount: fromAmountUsed,
                        toAmount: null,
                        includeTypeParam: true,
                        ct: ct
                    );

                    // Some deployments dislike "type=direct" (even though documented).
                    // Retry once without 'type' if needed.
                    if (dto is null)
                    {
                        dto = await CallEstimatedAmountAsync(
                            coinFrom: fromCandidate.coin,
                            networkFrom: fromCandidate.network,
                            coinTo: toCandidate.coin,
                            networkTo: toCandidate.network,
                            flow: flow,
                            type: "direct",
                            fromAmount: fromAmountUsed,
                            toAmount: null,
                            includeTypeParam: false,
                            ct: ct
                        );
                    }

                    if (dto is null) continue;

                    // ✅ Do NOT subtract WithdrawalFee again here.
                    // The estimated amount is typically already the "you get" value for that request.
                    var receiveForRequest = dto.ToAmount;
                    if (receiveForRequest <= 0m) continue;

                    // Normalize to a true unit rate: QUOTE per 1 BASE.
                    var unitRate = receiveForRequest / fromAmountUsed;
                    if (unitRate <= 0m) continue;

                    return new PriceResult(
                        Exchange: ExchangeKey,
                        Base: query.Base,
                        Quote: query.Quote,
                        Price: unitRate,
                        TimestampUtc: DateTimeOffset.UtcNow,
                        CorrelationId: dto.RateId,
                        Raw: null
                    );
                }
            }
        }

        return null;
    }

    // BUY = how much QUOTE is required to get 1 BASE
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        var (coinFromRaw, netFromRaw) = ResolveCoinAndNetwork(query.Quote); // paying QUOTE (USDT)
        var (coinToRaw, netToRaw) = ResolveCoinAndNetwork(query.Base);  // receiving BASE (XMR)

        if (string.IsNullOrWhiteSpace(coinFromRaw) || string.IsNullOrWhiteSpace(coinToRaw))
            return null;

        // Only standard floating flow — fixed-rate gives inflated reverse quotes
        var flows = BuildStandardFlowOnly();

        // Probe with a realistic USDT amount well above the minimum trade size.
        const decimal probeAmount = 500m;

        foreach (var flow in flows)
        {
            foreach (var fromCandidate in ExpandCurrencyCandidates(coinFromRaw, netFromRaw))
            {
                foreach (var toCandidate in ExpandCurrencyCandidates(coinToRaw, netToRaw))
                {
                    var dto = await CallEstimatedAmountAsync(
                        coinFrom: fromCandidate.coin,
                        networkFrom: fromCandidate.network,
                        coinTo: toCandidate.coin,
                        networkTo: toCandidate.network,
                        flow: flow,
                        type: "direct",
                        fromAmount: probeAmount,
                        toAmount: null,
                        includeTypeParam: true,
                        ct: ct
                    );

                    if (dto is null)
                    {
                        dto = await CallEstimatedAmountAsync(
                            coinFrom: fromCandidate.coin,
                            networkFrom: fromCandidate.network,
                            coinTo: toCandidate.coin,
                            networkTo: toCandidate.network,
                            flow: flow,
                            type: "direct",
                            fromAmount: probeAmount,
                            toAmount: null,
                            includeTypeParam: false,
                            ct: ct
                        );
                    }

                    if (dto is null) continue;

                    var grossXmr = dto.ToAmount;
                    if (grossXmr <= 0m) continue;

                    // ← FIX: API returns gross XMR before withdrawal fee for the buy direction.
                    // The website shows net-after-fee, so we subtract it to match.
                    // WithdrawalFee is in the output currency (XMR here).
                    var netXmr = grossXmr - Math.Max(0m, dto.WithdrawalFee);
                    if (netXmr <= 0m) continue;

                    // 500 USDT buys netXmr XMR → USDT per 1 XMR = 500 / netXmr
                    var usdtPerXmr = probeAmount / netXmr;
                    if (usdtPerXmr <= 0m) continue;

                    return new PriceResult(
                        Exchange: ExchangeKey,
                        Base: query.Base,
                        Quote: query.Quote,
                        Price: usdtPerXmr,
                        TimestampUtc: DateTimeOffset.UtcNow,
                        CorrelationId: dto.RateId,
                        Raw: null
                    );
                }
            }
        }

        return null;
    }

    private static List<string> BuildStandardFlowOnly()
        => new() { "standard" };

    // ← Only standard flow for buy — fixed-rate gives inflated reverse quotes
 
    // ← Keep original for sell (standard first, fixed-rate fallback is fine for direct quotes)
    private static List<string> BuildFlowAttempts(string? configuredFlow)
    {
        var list = new List<string>();

        var f = (configuredFlow ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(f))
            list.Add(f);

        if (!list.Contains("standard", StringComparer.OrdinalIgnoreCase))
            list.Add("standard");

        if (!list.Contains("fixed-rate", StringComparer.OrdinalIgnoreCase))
            list.Add("fixed-rate");

        return list;
    }

    // -------------------------
    // Estimated amount (core)
    // -------------------------

    private async Task<EstimatedAmountDto?> CallEstimatedAmountAsync(
        string coinFrom,
        string? networkFrom,
        string coinTo,
        string? networkTo,
        string flow,
        string type,                  // direct | reverse
        decimal? fromAmount,           // required for direct
        decimal? toAmount,             // required for reverse
        bool includeTypeParam,
        CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"fromCurrency={Uri.EscapeDataString(coinFrom)}",
            $"toCurrency={Uri.EscapeDataString(coinTo)}",
            $"flow={Uri.EscapeDataString(flow)}",
        };

        if (includeTypeParam)
            qs.Add($"type={Uri.EscapeDataString(type)}");

        if (!string.IsNullOrWhiteSpace(networkFrom))
            qs.Add($"fromNetwork={Uri.EscapeDataString(networkFrom)}");

        if (!string.IsNullOrWhiteSpace(networkTo))
            qs.Add($"toNetwork={Uri.EscapeDataString(networkTo)}");

        // ✅ IMPORTANT: do NOT send empty toAmount= / fromAmount=
        if (type.Equals("direct", StringComparison.OrdinalIgnoreCase))
        {
            if (fromAmount is null || fromAmount <= 0m) return null;
            qs.Add($"fromAmount={Uri.EscapeDataString(fromAmount.Value.ToString(CultureInfo.InvariantCulture))}");
        }
        else
        {
            if (toAmount is null || toAmount <= 0m) return null;
            qs.Add($"toAmount={Uri.EscapeDataString(toAmount.Value.ToString(CultureInfo.InvariantCulture))}");
        }

        // For fixed-rate flow, many setups expect useRateId=true (and will return rateId).
        // For standard flow, keep it false.
        var useRateId = flow.Equals("fixed-rate", StringComparison.OrdinalIgnoreCase);
        qs.Add($"useRateId={(useRateId ? "true" : "false")}");

        var url = "/v2/exchange/estimated-amount?" + string.Join("&", qs);

        var (status, body) = await SendForStringWithRetryAsync(
            requestFactory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaders(req);
                return req;
            },
            timeout: TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60)),
            retryCount: Math.Clamp(opt.RetryCount, 0, 6),
            ct: ct
        );

        if (status is null || string.IsNullOrWhiteSpace(body)) return null;
        if ((int)status < 200 || (int)status >= 300) return null;

        try
        {
            var dto = JsonSerializer.Deserialize<EstimatedAmountDto>(body, JsonOpts);
            if (dto is null) return null;

            // sanity: must include at least one amount field
            if (dto.FromAmount <= 0m && dto.ToAmount <= 0m) return null;

            return dto;
        }
        catch
        {
            return null;
        }
    }

    private sealed class EstimatedAmountDto
    {
        public string? FromCurrency { get; set; }
        public string? FromNetwork { get; set; }
        public string? ToCurrency { get; set; }
        public string? ToNetwork { get; set; }
        public string? Flow { get; set; }
        public string? Type { get; set; }

        public string? RateId { get; set; }
        public string? ValidUntil { get; set; }

        public decimal FromAmount { get; set; }
        public decimal ToAmount { get; set; }

        public decimal DepositFee { get; set; }
        public decimal WithdrawalFee { get; set; }
        public string? WarningMessage { get; set; }
    }



    // -------------------------
    // Currencies parsing
    // -------------------------

    private static List<ExchangeCurrency> ParseCurrencies(string json)
    {
        using var doc = JsonDocument.Parse(json);

        JsonElement arr;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            arr = doc.RootElement;
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty("data", out var data) &&
                 data.ValueKind == JsonValueKind.Array)
        {
            arr = data;
        }
        else
        {
            return new List<ExchangeCurrency>();
        }

        var list = new List<ExchangeCurrency>(arr.GetArrayLength());

        foreach (var el in arr.EnumerateArray())
        {
            var ticker =
                GetString(el, "ticker") ??
                GetString(el, "code") ??
                GetString(el, "currency") ??
                "";

            if (string.IsNullOrWhiteSpace(ticker)) continue;

            list.Add(new ExchangeCurrency(
                ExchangeId: ticker.Trim().ToLowerInvariant(),
                Ticker: ticker.Trim().ToUpperInvariant(),
                Network: ""
            ));
        }

        return list
            .OrderBy(x => x.Ticker, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Network, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static string? GetString(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty(name, out var p)) return null;
            return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }
    }

    // -------------------------
    // Coin+Network mapping
    // -------------------------

    private static (string coin, string? network) ResolveCoinAndNetwork(AssetRef asset)
    {
        var tickerLower = (asset.Ticker ?? "").Trim().ToLowerInvariant();
        var ex = (asset.ExchangeId ?? "").Trim();

        // 1) Internal style "usdt|arbitrum"
        if (!string.IsNullOrWhiteSpace(ex) && ex.Contains('|'))
        {
            var parts = ex.Split('|', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                var coin = parts[0].Trim().ToLowerInvariant();
                var net = NormalizeNetworkToChangeNow(parts[1]);
                return (coin, net);
            }
        }

        // 2) If ExchangeId looks like Wagyu-style concatenation (USDTARBITRUM / usdtarb / usdcarbitrum),
        // split based on the asset ticker prefix when possible.
        if (!string.IsNullOrWhiteSpace(ex) && !string.IsNullOrWhiteSpace(tickerLower))
        {
            var exLower = ex.Trim().ToLowerInvariant();

            if (exLower.StartsWith(tickerLower, StringComparison.OrdinalIgnoreCase) &&
                exLower.Length > tickerLower.Length)
            {
                var rest = exLower[tickerLower.Length..];

                var inferred = NormalizeNetworkToChangeNow(rest);
                if (!string.IsNullOrWhiteSpace(inferred))
                {
                    // Only accept if rest looked like a real network token
                    // (avoid splitting random tickers)
                    if (LooksLikeKnownNetwork(rest))
                        return (tickerLower, inferred);
                }
            }

            // Also support delimiters like "usdt-arbitrum" / "usdt_arbitrum"
            if (exLower.Contains('-') || exLower.Contains('_'))
            {
                var cleaned = exLower.Replace('_', '-');
                var bits = cleaned.Split('-', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (bits.Length == 2)
                {
                    var coin = bits[0].Trim().ToLowerInvariant();
                    var net = NormalizeNetworkToChangeNow(bits[1]);
                    if (!string.IsNullOrWhiteSpace(coin))
                        return (coin, net);
                }
            }
        }

        // 3) Default: use ExchangeId if present, else ticker; and map network from asset.Network
        var coin2 = (!string.IsNullOrWhiteSpace(asset.ExchangeId) ? asset.ExchangeId : asset.Ticker)
            .Trim()
            .ToLowerInvariant();

        var net2 = NormalizeNetworkToChangeNow(asset.Network);
        return (coin2, net2);
    }

    private static bool LooksLikeKnownNetwork(string token)
    {
        var t = (token ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return false;

        // common network tokens / abbreviations
        return t is
            "btc" or "bitcoin" or
            "eth" or "ethereum" or "erc20" or
            "trx" or "tron" or "trc20" or
            "bsc" or "bep20" or
            "sol" or "solana" or
            "arbitrum" or "arb" or
            "base" or
            "matic" or "polygon";
    }

    private static IEnumerable<(string coin, string? network)> ExpandCurrencyCandidates(string coinRaw, string? networkRaw)
    {
        var coin = (coinRaw ?? "").Trim().ToLowerInvariant();
        var net = (networkRaw ?? "").Trim().ToLowerInvariant();
        if (coin.Length == 0) yield break;

        // 1) Exact (coin + network)
        if (!string.IsNullOrWhiteSpace(net))
            yield return (coin, net);

        // 2) Network synonyms (still as network param)
        if (!string.IsNullOrWhiteSpace(net))
        {
            foreach (var n2 in ExpandNetworkSynonyms(net))
            {
                if (!n2.Equals(net, StringComparison.OrdinalIgnoreCase))
                    yield return (coin, n2);
            }
        }

        // 3) Drop network entirely (lets API default)
        if (!string.IsNullOrWhiteSpace(net))
            yield return (coin, null);

        // 4) Combined-code fallback (some ChangeNOW setups use network-specific currency codes)
        if (!string.IsNullOrWhiteSpace(net))
        {
            foreach (var combined in ExpandCombinedCurrencyCodes(coin, net))
                yield return (combined, null);
        }

        // 5) If no network at all, just coin
        if (string.IsNullOrWhiteSpace(net))
            yield return (coin, null);
    }

    private static IEnumerable<string> ExpandNetworkSynonyms(string net)
    {
        var n = (net ?? "").Trim().ToLowerInvariant();

        // Keep the original first; caller already yielded it.
        // Add common alternates.
        if (n == "eth") { yield return "erc20"; yield break; }
        if (n == "erc20") { yield return "eth"; yield break; }

        if (n == "trx") { yield return "trc20"; yield break; }
        if (n == "trc20") { yield return "trx"; yield break; }

        if (n == "bsc") { yield return "bep20"; yield break; }
        if (n == "bep20") { yield return "bsc"; yield break; }

        if (n == "arbitrum") { yield return "arb"; yield break; }
        if (n == "arb") { yield return "arbitrum"; yield break; }

        // else: no synonyms
        yield break;
    }

    private static IEnumerable<string> ExpandCombinedCurrencyCodes(string coin, string net)
    {
        // Best-effort common patterns used by aggregators.
        // If ChangeNOW doesn’t support them, they simply won’t resolve and we’ll continue.
        var c = coin.ToLowerInvariant();
        var n = net.ToLowerInvariant();

        // common stablecoins: usdt/usdc
        if (c is "usdt" or "usdc")
        {
            if (n is "eth" or "erc20") { yield return c + "eth"; yield return c + "erc20"; }
            else if (n is "trx" or "trc20") { yield return c + "trx"; yield return c + "trc20"; }
            else if (n is "bsc" or "bep20") { yield return c + "bsc"; yield return c + "bep20"; }
            else if (n is "arbitrum" or "arb") { yield return c + "arb"; yield return c + "arbitrum"; }
            else if (n is "matic" or "polygon") { yield return c + "matic"; yield return c + "polygon"; }
            else if (n is "sol" or "solana") { yield return c + "sol"; yield return c + "solana"; }
            else if (n is "base") { yield return c + "base"; }
            yield break;
        }

        // generic: coin + network token
        yield return c + n;
    }

    private static string? NormalizeNetworkToChangeNow(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return null;

        var n = network.Trim();

        return n switch
        {
            "Bitcoin" => "btc",
            "BTC" => "btc",
            "btc" => "btc",
            "bitcoin" => "btc",

            "Ethereum" => "eth",
            "ERC20" => "eth",
            "ETH" => "eth",
            "eth" => "eth",
            "erc20" => "eth",

            "Tron" => "trx",
            "TRX" => "trx",
            "TRC20" => "trx",
            "trx" => "trx",
            "tron" => "trx",
            "trc20" => "trx",

            "Binance Smart Chain" => "bsc",
            "BSC" => "bsc",
            "BEP20" => "bsc",
            "bsc" => "bsc",
            "bep20" => "bsc",

            "Solana" => "sol",
            "SOL" => "sol",
            "sol" => "sol",
            "solana" => "sol",

            "Arbitrum" => "arbitrum",
            "Arbitrum One" => "arbitrum",
            "ARB" => "arbitrum",
            "arb" => "arbitrum",
            "arbitrum" => "arbitrum",

            "Base" => "base",
            "base" => "base",

            "Polygon" => "matic",
            "MATIC" => "matic",
            "matic" => "matic",
            "polygon" => "matic",

            "Litecoin" => "ltc",
            "LTC" => "ltc",
            "ltc" => "ltc",

            _ => Slug(n)
        };

        static string Slug(string s)
        {
            var x = s.ToLowerInvariant();
            x = x.Replace("(", "").Replace(")", "");
            x = x.Replace("-", "").Replace("_", "");
            x = x.Replace(" ", "");
            return x.Length == 0 ? "" : x;
        }
    }

    // -------------------------
    // Headers + request sender
    // -------------------------

    private void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("x-changenow-api-key", opt.ApiKey);

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
        if (n == 429) return true;                              // rate limited
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