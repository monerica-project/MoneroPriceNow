using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implmentations.PegasusSwap;

public sealed class PegasusSwapClient : IPegasusSwapClient
{
    private readonly HttpClient http;
    private readonly PegasusSwapOptions opt;

    public string  ExchangeKey => "pegasusswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    // typeSwap=2 → float (typeSwap=1 is fixed — DO NOT USE for price comparison)
    private const string TypeSwapFloat = "2";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public PegasusSwapClient(HttpClient http, IOptions<PegasusSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
        this.http.Timeout = Timeout.InfiniteTimeSpan;
    }

    // -------------------------
    // Currencies
    // -------------------------

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var deposit = await GetAllCoinsAsync(method: 1, ct);
        var withdraw = await GetAllCoinsAsync(method: 2, ct);

        return deposit.Concat(withdraw)
            .GroupBy(x => $"{x.ExchangeId}|{x.Ticker}|{x.Network}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.PublicKey) || string.IsNullOrWhiteSpace(opt.Secret))
            return null;

        var coinFrom = ResolveCoin(query.Base);
        var coinTo = ResolveCoin(query.Quote);
        if (string.IsNullOrWhiteSpace(coinFrom) || string.IsNullOrWhiteSpace(coinTo)) return null;

        var networkFrom = NormalizeNetworkToPegasus(query.Base.Network);
        var networkTo = NormalizeNetworkToPegasus(query.Quote.Network);

        // ← Use exactly 1 XMR — matches what the website quotes for sell.
        // Larger probes (5 XMR) get a better rate than 1 XMR on this exchange,
        // which overstates the sell price.
        const decimal probeAmount = 1m;

        var dto = await CallExchangeCoinAsync(
            amount: probeAmount,
            coinFrom: coinFrom,
            coinTo: coinTo,
            networkFrom: networkFrom,
            networkTo: networkTo,
            lastSource: "deposit",
            ct: ct
        );

        if (dto is null) return null;

        var quoteReceived = (decimal)dto.Receive;
        if (quoteReceived <= 0) return null;

        // With probe=1, dto.Receive is already USDT per 1 XMR directly
        var unitRate = quoteReceived / probeAmount;
        if (unitRate <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: unitRate,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }


    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.PublicKey) || string.IsNullOrWhiteSpace(opt.Secret))
            return null;

        // BUY: probe with 500 USDT using lastSource=deposit → get XMR back
        // Rate = 500 / dto.Receive = USDT per 1 XMR
        // (lastSource=receive gives a distorted/worse rate for this exchange)
        var coinFrom = ResolveCoin(query.Quote); // paying QUOTE (USDT)
        var coinTo = ResolveCoin(query.Base);  // receiving BASE (XMR)
        if (string.IsNullOrWhiteSpace(coinFrom) || string.IsNullOrWhiteSpace(coinTo)) return null;

        var networkFrom = NormalizeNetworkToPegasus(query.Quote.Network);
        var networkTo = NormalizeNetworkToPegasus(query.Base.Network);

        const decimal probeUsdt = 500m;

        var dto = await CallExchangeCoinAsync(
            amount: probeUsdt,
            coinFrom: coinFrom,     // USDT
            coinTo: coinTo,       // XMR
            networkFrom: networkFrom,
            networkTo: networkTo,
            lastSource: "deposit",    // "I am depositing 500 USDT"
            ct: ct
        );

        if (dto is null) return null;

        var xmrReceived = (decimal)dto.Receive;
        if (xmrReceived <= 0) return null;

        // USDT per 1 XMR = 500 / xmrReceived
        var usdtPerXmr = probeUsdt / xmrReceived;
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
    // -------------------------
    // Core API call
    // -------------------------

    private async Task<ExchangeCoinResponse?> CallExchangeCoinAsync(
        decimal amount,
        string coinFrom,
        string coinTo,
        string? networkFrom,
        string? networkTo,
        string lastSource,
        CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"amount={amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"coinFrom={Uri.EscapeDataString(coinFrom)}",
            $"coinTo={Uri.EscapeDataString(coinTo)}",
            $"lastSource={Uri.EscapeDataString(lastSource)}",
            $"typeSwap={TypeSwapFloat}"   // ← 2 = float, 1 = fixed
        };

        if (!string.IsNullOrWhiteSpace(networkFrom))
            qs.Add($"networkFrom={Uri.EscapeDataString(networkFrom)}");
        if (!string.IsNullOrWhiteSpace(networkTo))
            qs.Add($"networkTo={Uri.EscapeDataString(networkTo)}");

        var url = "/api/private/exchange-coin?" + string.Join("&", qs);

        var (status, raw) = await SendForStringWithRetryAsync(
            requestFactory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddPegasusAuthHeaders(req, payload: "exchange-coin");
                return req;
            },
            timeout: GetRequestTimeout(),
            retryCount: GetRetryCount(),
            ct: ct
        );

        if (status is null || string.IsNullOrWhiteSpace(raw)) return null;
        if ((int)status < 200 || (int)status >= 300) return null;

        try
        {
            var dto = JsonSerializer.Deserialize<ExchangeCoinResponse>(raw, JsonOpts);
            if (dto is null) return null;
            if (dto.Receive <= 0 && dto.Amount <= 0) return null;
            return dto;
        }
        catch
        {
            return null;
        }
    }

    // -------------------------
    // Currencies helpers
    // -------------------------

    private async Task<List<ExchangeCurrency>> GetAllCoinsAsync(int method, CancellationToken ct)
    {
        const int limit = 500;
        var results = new List<ExchangeCurrency>();

        var first = await GetAllCoinsPageAsync(page: 1, limit: limit, method: method, ct);
        results.AddRange(ExtractCurrencies(first, method));

        var pageCount = first?.Pagination?.PageCount ?? 1;
        for (var page = 2; page <= pageCount; page++)
        {
            var next = await GetAllCoinsPageAsync(page, limit, method, ct);
            results.AddRange(ExtractCurrencies(next, method));
        }

        return results;
    }

    private async Task<GetAllCoinsResponse?> GetAllCoinsPageAsync(int page, int limit, int method, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.PublicKey) || string.IsNullOrWhiteSpace(opt.Secret))
            return null;

        var url = $"/api/private/get-all-coins?page={page}&limit={limit}&method={method}";

        var (status, raw) = await SendForStringWithRetryAsync(
            requestFactory: () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddPegasusAuthHeaders(req, payload: "get-all-coins");
                return req;
            },
            timeout: GetRequestTimeout(),
            retryCount: GetRetryCount(),
            ct: ct
        );

        if (status is null || string.IsNullOrWhiteSpace(raw) || (int)status < 200 || (int)status >= 300)
            return null;

        try { return JsonSerializer.Deserialize<GetAllCoinsResponse>(raw, JsonOpts); }
        catch { return null; }
    }

    private static IEnumerable<ExchangeCurrency> ExtractCurrencies(GetAllCoinsResponse? dto, int method)
    {
        if (dto is null) yield break;

        var coins = method == 1 ? dto.DepositCoins : dto.WithdrawCoins;
        if (coins is null) yield break;

        foreach (var c in coins)
        {
            var ticker = (c.SubName ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(ticker)) continue;

            var exchangeId = ticker.ToLowerInvariant();

            var nets = method == 1 ? c.Networks?.Deposits : c.Networks?.Withdraws;
            if (nets is null || nets.Count == 0)
            {
                yield return new ExchangeCurrency(exchangeId, ticker, "");
                continue;
            }

            foreach (var n in nets)
            {
                var code = (n.SubName ?? "").Trim();
                var friendly = NormalizeNetworkFromPegasus(code);
                yield return new ExchangeCurrency(exchangeId, ticker, friendly);
            }
        }
    }

    // -------------------------
    // Auth headers
    // -------------------------

    private void AddPegasusAuthHeaders(HttpRequestMessage req, string payload)
    {
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("x-api-public-key", opt.PublicKey);
        req.Headers.TryAddWithoutValidation("x-api-payload", payload);
        req.Headers.TryAddWithoutValidation("x-api-signature", ComputeSignatureHex(payload, opt.Secret));

        if (!req.Headers.UserAgent.Any() && !string.IsNullOrWhiteSpace(opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);
    }

    private static string ComputeSignatureHex(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA512(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));

        return sb.ToString();
    }

    // -------------------------
    // Network mapping
    // -------------------------

    private static string ResolveCoin(AssetRef asset)
    {
        var id = (asset.ExchangeId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(id)) return id.ToLowerInvariant();
        return (asset.Ticker ?? "").Trim().ToLowerInvariant();
    }

    private static string? NormalizeNetworkToPegasus(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return null;

        return network.Trim() switch
        {
            "Tron" => "TRX",
            "TRX" => "TRX",
            "TRC20" => "TRX",
            "Ethereum" => "ERC20",
            "ETH" => "ERC20",
            "ERC20" => "ERC20",
            "Binance Smart Chain" => "BSC",
            "BSC" => "BSC",
            "BEP20" => "BSC",
            "Solana" => "SOL",
            "SOL" => "SOL",
            "Bitcoin" => "BTC",
            "BTC" => "BTC",
            "Polygon" => "MATIC",
            "MATIC" => "MATIC",
            "Arbitrum" => "ARB",
            "ARB" => "ARB",
            _ => network.Trim()
        };
    }

    private static string NormalizeNetworkFromPegasus(string networkCode)
    {
        if (string.IsNullOrWhiteSpace(networkCode)) return "";

        return networkCode.Trim().ToUpperInvariant() switch
        {
            "TRX" => "Tron",
            "ERC20" => "Ethereum",
            "BSC" => "Binance Smart Chain",
            "SOL" => "Solana",
            "BTC" => "Bitcoin",
            "MATIC" => "Polygon",
            "ARB" => "Arbitrum",
            _ => networkCode.Trim()
        };
    }

    // -------------------------
    // Timeouts / retry config
    // -------------------------

    private TimeSpan GetRequestTimeout()
    {
        var seconds = opt.TimeoutSeconds <= 0 ? 15 : opt.TimeoutSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 60));
    }

    private int GetRetryCount() => 1;

    // -------------------------
    // HTTP sender
    // -------------------------

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
        return code == HttpStatusCode.RequestTimeout || n == 429 || (n >= 500 && n <= 599);
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = Math.Min(2000, (int)(200 * Math.Pow(2, attempt)));
        ms += Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }

    // -------------------------
    // DTOs
    // -------------------------

    private sealed class GetAllCoinsResponse
    {
        public PaginationDto? Pagination { get; set; }
        public List<CoinDto>? DepositCoins { get; set; }
        public List<CoinDto>? WithdrawCoins { get; set; }
    }

    private sealed class PaginationDto
    {
        public int PageCount { get; set; }
        public int TotalCount { get; set; }
    }

    private sealed class CoinDto
    {
        public string? SubName { get; set; }
        public string? Name { get; set; }
        public NetworksDto? Networks { get; set; }
    }

    private sealed class NetworksDto
    {
        public List<NetworkDto>? Deposits { get; set; }
        public List<NetworkDto>? Withdraws { get; set; }
    }

    private sealed class NetworkDto
    {
        public string? SubName { get; set; }
        public bool MemoNeeded { get; set; }
    }

    private sealed class ExchangeCoinResponse
    {
        public string? Pair { get; set; }
        public decimal Amount { get; set; }  // USDT to send (lastSource=receive)
        public double ExchangeRate { get; set; }
        public double Receive { get; set; }  // BASE/QUOTE received (lastSource=deposit)
        public double MinAmount { get; set; }
        public double MaxAmount { get; set; }
    }
}