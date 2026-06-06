using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class ExolixClient : IExolixClient
{
    private readonly HttpClient http;
    private readonly ExolixOptions opt;

    public string ExchangeKey => "exolix";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    // MinAmountUsd: live API minimum when available (set in buy, where coinFrom = USDT ≈ USD).
    private decimal? _apiMinAmountUsd;
    private readonly object _apiMinAmountLock = new();
    public decimal MinAmountUsd => _apiMinAmountUsd ?? opt.MinAmountUsd;

    // Always use float — fixed rates are consistently inflated.
    private const string RateTypeFloat = "float";

    // Exolix does NOT carry USDT on Tron, and routes XMR<->USDT on EVM/SOL chains.
    // USDT is ~$1 on every chain, so any of these is a valid USDT/XMR price. ETH first;
    // the working one is memoized so steady-state is a single rate call.
    private static readonly string[] UsdtNetPref =
        { "ETH", "SOL", "BSC", "MATIC", "ARBITRUM", "OPTIMISM", "AVAXC" };
    private volatile string? _usdtNet;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public ExolixClient(HttpClient http, IOptions<ExolixOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
        this.http.Timeout = Timeout.InfiniteTimeSpan;
    }

    // ------------------------------------------------------------
    // Currency list
    // ------------------------------------------------------------
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var size = Math.Clamp(opt.CurrenciesPageSize, 10, 500);
        var page = 1;
        var all = new List<ExchangeCurrency>(capacity: 512);
        int? totalCount = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = $"currencies?page={page}&size={size}&withNetworks=true";
            var (status, raw) = await SendForStringWithRetryAsync(
                requestFactory: () => CreateJsonRequest(HttpMethod.Get, url),
                timeout: GetRequestTimeout(),
                retryCount: GetRetryCount(),
                ct: ct
            );

            if (status is null || string.IsNullOrWhiteSpace(raw) || (int)status < 200 || (int)status >= 300)
                break;

            CurrenciesResponse? dto;
            try { dto = JsonSerializer.Deserialize<CurrenciesResponse>(raw, JsonOpts); }
            catch { break; }

            if (dto?.Data is null || dto.Data.Count == 0) break;

            totalCount ??= dto.Count;

            foreach (var c in dto.Data)
            {
                var code = (c.Code ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(code)) continue;

                if (c.Networks is { Count: > 0 })
                {
                    foreach (var n in c.Networks)
                    {
                        var netCode = (n.Network ?? "").Trim().ToUpperInvariant();
                        if (string.IsNullOrWhiteSpace(netCode)) continue;

                        var exchangeId = $"{code}|{netCode}".ToLowerInvariant();
                        var friendly = FriendlyNetworkFromExolix(netCode, n.Name, n.ShortName);

                        all.Add(new ExchangeCurrency(
                            ExchangeId: exchangeId,
                            Ticker: code,
                            Network: friendly
                        ));
                    }
                }
                else
                {
                    all.Add(new ExchangeCurrency(
                        ExchangeId: code.ToLowerInvariant(),
                        Ticker: code,
                        Network: code.Equals("XMR", StringComparison.OrdinalIgnoreCase) ? "Mainnet" : ""
                    ));
                }
            }

            if (totalCount is not null && page * size >= totalCount.Value) break;
            if (page >= 50) break;
            page++;
        }

        return all
            .GroupBy(x => $"{x.ExchangeId}|{x.Ticker}|{x.Network}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ticker)
            .ThenBy(x => x.Network)
            .ToList();
    }

    // ------------------------------------------------------------
    // Prices
    // ------------------------------------------------------------
    public Task<PriceResult?> GetPriceAsync(PriceQuery query, CancellationToken ct = default)
        => GetSellPriceAsync(query, ct);

    // SELL: send 1 XMR → read toAmount (USDT received) directly.
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var rate = await QuoteXmrUsdtAsync(query, usdtIsFrom: false, amount: 1m, ct);
        if (rate is null || rate.FromAmount <= 0 || rate.ToAmount <= 0) return null;

        var px = rate.ToAmount / rate.FromAmount;
        if (px <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, px, DateTimeOffset.UtcNow);
    }

    // BUY: how much USDT to send to receive 1 XMR.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var rate = await QuoteXmrUsdtAsync(query, usdtIsFrom: true, amount: 500m, ct);
        if (rate is null || rate.FromAmount <= 0 || rate.ToAmount <= 0) return null;

        // coinFrom is USDT here, so rate.MinAmount is in USDT ≈ USD.
        if (rate.MinAmount > 0)
        {
            lock (_apiMinAmountLock)
                _apiMinAmountUsd = rate.MinAmount;
        }

        var usdtPerXmr = rate.FromAmount / rate.ToAmount;
        if (usdtPerXmr <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, usdtPerXmr, DateTimeOffset.UtcNow);
    }

    // Quote XMR<->USDT, resolving the USDT side to a network Exolix actually pairs with
    // XMR (never Tron). Tries the memoized network first, then the preference ladder.
    private async Task<RateResponse?> QuoteXmrUsdtAsync(
        PriceQuery query, bool usdtIsFrom, decimal amount, CancellationToken ct)
    {
        var xmr  = (query.Base.Ticker  ?? "XMR").Trim().ToUpperInvariant();
        var usdt = (query.Quote.Ticker ?? "USDT").Trim().ToUpperInvariant();

        var candidates = new List<string>();
        var memo = _usdtNet;
        if (memo is not null) candidates.Add(memo);
        foreach (var n in UsdtNetPref)
            if (!candidates.Contains(n, StringComparer.OrdinalIgnoreCase)) candidates.Add(n);

        foreach (var net in candidates)
        {
            // XMR side needs no network; only the USDT side is network-qualified.
            var rate = usdtIsFrom
                ? await GetRateAsync(usdt, net, xmr, null, amount, ct)   // BUY:  USDT/net -> XMR
                : await GetRateAsync(xmr, null, usdt, net, amount, ct);  // SELL: XMR -> USDT/net

            if (rate is not null && rate.FromAmount > 0 && rate.ToAmount > 0)
            {
                if (!string.Equals(_usdtNet, net, StringComparison.OrdinalIgnoreCase))
                {
                    _usdtNet = net;
                    Console.WriteLine($"[EXOLIX] resolved USDT network = {net} for the XMR pair");
                }
                return rate;
            }
        }

        return null;
    }

    // ------------------------------------------------------------
    // Core rate call
    // ------------------------------------------------------------
    private async Task<RateResponse?> GetRateAsync(
        string coinFrom, string? networkFrom,
        string coinTo, string? networkTo,
        decimal amount, CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"coinFrom={Uri.EscapeDataString(coinFrom)}",
            $"coinTo={Uri.EscapeDataString(coinTo)}",
            $"amount={amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"rateType={RateTypeFloat}"
        };

        if (!string.IsNullOrWhiteSpace(networkFrom))
            qs.Add($"networkFrom={Uri.EscapeDataString(networkFrom)}");
        if (!string.IsNullOrWhiteSpace(networkTo))
            qs.Add($"networkTo={Uri.EscapeDataString(networkTo)}");

        var url = "rate?" + string.Join("&", qs);

        var (status, raw) = await SendForStringWithRetryAsync(
            requestFactory: () => CreateJsonRequest(HttpMethod.Get, url),
            timeout: GetRequestTimeout(),
            retryCount: GetRetryCount(),
            ct: ct
        );

        // 422 = "Such exchange pair is not available" for this network → try the next one.
        if (status is null || string.IsNullOrWhiteSpace(raw) || (int)status < 200 || (int)status >= 300)
            return null;

        try { return JsonSerializer.Deserialize<RateResponse>(raw, JsonOpts); }
        catch { return null; }
    }

    // ------------------------------------------------------------
    // Request creation + sender
    // ------------------------------------------------------------
    private HttpRequestMessage CreateJsonRequest(HttpMethod method, string relativeUrl)
    {
        var req = new HttpRequestMessage(method, relativeUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        // NOTE: /currencies and /rate are PUBLIC. Sending an Authorization header with a
        // non-key value makes Exolix 400 the request, which is what broke the currency
        // lookup. Auth is only needed for transaction creation, so it's intentionally
        // not sent here.

        if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);

        return req;
    }

    private TimeSpan GetRequestTimeout()
    {
        var seconds = opt.TimeoutSeconds <= 0 ? 15 : opt.TimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 60));
    }

    private int GetRetryCount() => Math.Clamp(opt.RetryCount, 0, 6);

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
                var body = await resp.Content.ReadAsStringAsync(cts.Token);

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
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return (null, null);
            }
            catch (HttpRequestException)
            {
                resp?.Dispose();
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return (null, null);
            }
            finally { resp?.Dispose(); }
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
        var ms = Math.Min(2000, (int)(200 * Math.Pow(2, attempt)));
        ms += Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }

    // ------------------------------------------------------------
    // Network friendly-name mapping (for currency listing)
    // ------------------------------------------------------------
    private static string FriendlyNetworkFromExolix(string netCode, string? name, string? shortName)
    {
        var sc = (shortName ?? "").Trim().ToUpperInvariant();
        var nm = (name ?? "").Trim();

        if (sc == "ERC20") return "Ethereum";
        if (sc == "TRC20") return "Tron";
        if (sc == "BEP20") return "Binance Smart Chain";
        if (sc == "SOL") return "Solana";

        return netCode.ToUpperInvariant() switch
        {
            "ETH" => "Ethereum",
            "TRX" => "Tron",
            "BSC" => "Binance Smart Chain",
            "SOL" => "Solana",
            "XMR" => "Mainnet",
            _ => string.IsNullOrWhiteSpace(nm) ? netCode : nm
        };
    }

    // ------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------
    private sealed class CurrenciesResponse
    {
        public List<CurrencyDto> Data { get; set; } = new();
        public int Count { get; set; }
    }

    private sealed class CurrencyDto
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public string? Notes { get; set; }
        public List<NetworkDto>? Networks { get; set; }
    }

    private sealed class NetworkDto
    {
        public string? Network { get; set; }
        public string? Name { get; set; }
        public string? ShortName { get; set; }
        public bool IsDefault { get; set; }
        public int Precision { get; set; }
        public bool MemoNeeded { get; set; }
        public string? MemoName { get; set; }
        public string? Contract { get; set; }
        public string? Icon { get; set; }
    }

    private sealed class RateResponse
    {
        [JsonPropertyName("fromAmount")] public decimal FromAmount { get; set; }
        [JsonPropertyName("toAmount")] public decimal ToAmount { get; set; }
        [JsonPropertyName("rate")] public decimal Rate { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("minAmount")] public decimal MinAmount { get; set; }
        [JsonPropertyName("withdrawMin")] public decimal WithdrawMin { get; set; }
        [JsonPropertyName("maxAmount")] public decimal MaxAmount { get; set; }
    }
}