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

    public string  ExchangeKey => "exolix";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;

    // Always use float — fixed rates are consistently inflated
    private const string RateTypeFloat = "float";

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

    // SELL: send 1 XMR → read toAmount (USDT received) directly
    // amount=1 is fine here — sell at 1 XMR matches what the website quotes
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (coinFrom, netFrom) = ResolveCoinAndNetwork(query.Base);
        var (coinTo, netTo) = ResolveCoinAndNetwork(query.Quote);

        if (string.IsNullOrWhiteSpace(coinFrom) || string.IsNullOrWhiteSpace(coinTo))
            return null;

        var rate = await GetRateAsync(
            coinFrom: coinFrom,
            networkFrom: netFrom,
            coinTo: coinTo,
            networkTo: netTo,
            amount: 1m,       // send 1 XMR
            withdrawalAmount: null,
            ct: ct
        );

        if (rate is null || rate.FromAmount <= 0 || rate.ToAmount <= 0)
            return null;

        // toAmount / fromAmount = USDT per XMR
        var px = rate.ToAmount / rate.FromAmount;
        if (px <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: px,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // BUY: how much USDT to send to receive 1 XMR
    // Probe with 500 USDT (coinFrom=USDT, coinTo=XMR), divide:
    // USDT per XMR = 500 / toAmount
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (baseCoin, baseNet) = ResolveCoinAndNetwork(query.Base);   // XMR
        var (quoteCoin, quoteNet) = ResolveCoinAndNetwork(query.Quote);  // USDT

        if (string.IsNullOrWhiteSpace(baseCoin) || string.IsNullOrWhiteSpace(quoteCoin))
            return null;

        // Probe: send 500 USDT → get XMR back
        // rate.toAmount = XMR received for 500 USDT
        // USDT per 1 XMR = 500 / toAmount
        const decimal probeUsdt = 500m;

        var rate = await GetRateAsync(
            coinFrom: quoteCoin,   // USDT
            networkFrom: quoteNet,
            coinTo: baseCoin,    // XMR
            networkTo: baseNet,
            amount: probeUsdt,   // ← probe 500 USDT
            withdrawalAmount: null,        // ← do NOT use withdrawalAmount; distorts rate
            ct: ct
        );

        if (rate is null || rate.FromAmount <= 0 || rate.ToAmount <= 0)
            return null;

        // rate.fromAmount = USDT actually used (should equal probe)
        // rate.toAmount   = XMR received
        // USDT per 1 XMR = fromAmount / toAmount
        var usdtPerXmr = rate.FromAmount / rate.ToAmount;
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

    // ------------------------------------------------------------
    // Core rate call
    // ------------------------------------------------------------
    private async Task<RateResponse?> GetRateAsync(
        string coinFrom,
        string? networkFrom,
        string coinTo,
        string? networkTo,
        decimal amount,
        decimal? withdrawalAmount,
        CancellationToken ct)
    {
        var qs = new List<string>
        {
            $"coinFrom={Uri.EscapeDataString(coinFrom)}",
            $"coinTo={Uri.EscapeDataString(coinTo)}",
            $"amount={amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"rateType={RateTypeFloat}"   // ← always float
        };

        if (!string.IsNullOrWhiteSpace(networkFrom))
            qs.Add($"networkFrom={Uri.EscapeDataString(networkFrom)}");
        if (!string.IsNullOrWhiteSpace(networkTo))
            qs.Add($"networkTo={Uri.EscapeDataString(networkTo)}");

        if (withdrawalAmount is not null && withdrawalAmount.Value > 0)
            qs.Add($"withdrawalAmount={withdrawalAmount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        var url = "rate?" + string.Join("&", qs);

        var (status, raw) = await SendForStringWithRetryAsync(
            requestFactory: () => CreateJsonRequest(HttpMethod.Get, url),
            timeout: GetRequestTimeout(),
            retryCount: GetRetryCount(),
            ct: ct
        );

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

        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
            req.Headers.TryAddWithoutValidation("Authorization", opt.ApiKey);

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
    // Symbol + network mapping
    // ------------------------------------------------------------
    private static (string coin, string? network) ResolveCoinAndNetwork(AssetRef asset)
    {
        var ex = (asset.ExchangeId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(ex))
        {
            if (ex.Contains('|'))
            {
                var parts = ex.Split('|', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                    return (parts[0].Trim().ToUpperInvariant(), NormalizeNetworkToExolix(parts[1]));
            }
            else
            {
                var coinOnly = ex.Trim().ToUpperInvariant();
                if (coinOnly.All(char.IsLetterOrDigit))
                    return (coinOnly, null);
            }
        }

        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ticker)) return ("", null);

        return (ticker, NormalizeNetworkToExolix(asset.Network));
    }

    private static string? NormalizeNetworkToExolix(string? network)
    {
        if (string.IsNullOrWhiteSpace(network)) return null;

        var n = network.Trim();

        var mapped = n switch
        {
            "Tron" => "TRX",
            "TRC20" => "TRX",
            "Ethereum" => "ETH",
            "ERC20" => "ETH",
            "Binance Smart Chain" => "BSC",
            "BEP20" => "BSC",
            "Solana" => "SOL",
            "Bitcoin" => "BTC",
            "Monero" => "XMR",
            "Litecoin" => "LTC",
            "Arbitrum" => "ARB",
            "Base" => "BASE",
            _ => ""
        };

        if (!string.IsNullOrWhiteSpace(mapped)) return mapped;

        var upper = n.ToUpperInvariant();
        if (upper.Length <= 16 && upper.All(char.IsLetterOrDigit)) return upper;

        return null;
    }

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