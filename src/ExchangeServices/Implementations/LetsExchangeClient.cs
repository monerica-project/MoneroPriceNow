using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class LetsExchangeClient : ILetsExchangeClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient http;
    private readonly LetsExchangeOptions opt;

    public string  ExchangeKey => "letsexchange";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public LetsExchangeClient(HttpClient http, IOptions<LetsExchangeOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> ? USDT(TRC20)
    // POST /v1/info
    // Response.amount = final amount user will receive
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!IsConfigured()) return null;

        var (fromCoin, fromNet) = ResolveCoinAndNetwork(query.Base);
        var (toCoin, toNet) = ResolveCoinAndNetwork(query.Quote);

        // Try 1 first, but if min_amount > 1, re-try with min
        var amtIn = 1m;

        var info = await PostInfoAsync(isRevert: false, fromCoin, toCoin, fromNet, toNet, amtIn, ct);
        if (info is null) return null;

        if (info.MinAmount > 0 && amtIn < info.MinAmount)
        {
            amtIn = RoundUp(info.MinAmount * 1.05m);
            info = await PostInfoAsync(isRevert: false, fromCoin, toCoin, fromNet, toNet, amtIn, ct);
            if (info is null) return null;
        }

        if (info.Amount <= 0) return null;

        var price = info.Amount / amtIn; // quote per 1 base
        if (price <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: price,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null
        );
    }

    // =========================
    // BUY: ? USDT(TRC20) needed to receive 1 XMR
    // POST /v1/info-revert (amount = withdrawal_amount)
    // Response.amount = amount to send (from coin)
    // =========================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!IsConfigured()) return null;

        var (fromCoin, fromNet) = ResolveCoinAndNetwork(query.Quote); // pay USDT
        var (toCoin, toNet) = ResolveCoinAndNetwork(query.Base);      // receive XMR

        // target 1 XMR; if it fails due to min withdrawal, try smaller then scale to 1
        var targets = new[] { 1m, 0.5m, 0.2m, 0.1m, 0.05m };

        foreach (var want in targets)
        {
            var info = await PostInfoAsync(isRevert: true, fromCoin, toCoin, fromNet, toNet, want, ct);
            if (info is null || info.Amount <= 0) continue;

            var usdtNeeded = info.Amount;          // amount to send
            var perXmr = usdtNeeded / want;        // normalize to 1 XMR
            if (perXmr <= 0) continue;

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: perXmr,
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: null,
                Raw: null
            );
        }

        return null;
    }

    // =========================
    // CURRENCIES: GET /v2/coins
    // Expands each coin into (coin + network) variants:
    // ExchangeId = "USDT:TRC20"
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (!IsConfigured()) return Array.Empty<ExchangeCurrency>();

        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

        var res = await http.SendForStringWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "v2/coins");
            AddAuth(req);
            return req;
        }, timeout, opt.RetryCount, ct);

        if (res is null) return Array.Empty<ExchangeCurrency>();
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices) return Array.Empty<ExchangeCurrency>();

        try
        {
            using var doc = JsonDocument.Parse(res.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>();

            foreach (var coinEl in doc.RootElement.EnumerateArray())
            {
                var coin = coinEl.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(coin)) continue;

                var isActive = coinEl.TryGetProperty("is_active", out var aEl) && aEl.ValueKind == JsonValueKind.Number && aEl.TryGetInt32(out var ai) && ai == 1;
                if (!isActive) continue;

                if (coinEl.TryGetProperty("networks", out var netsEl) && netsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in netsEl.EnumerateArray())
                    {
                        var netActive = n.TryGetProperty("is_active", out var naEl) && naEl.ValueKind == JsonValueKind.Number && naEl.TryGetInt32(out var nai) && nai == 1;
                        if (!netActive) continue;

                        var netCode = n.TryGetProperty("code", out var ncEl) ? ncEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(netCode)) continue;

                        var netName = n.TryGetProperty("name", out var nnEl) ? nnEl.GetString() : null;

                        list.Add(new ExchangeCurrency(
                            ExchangeId: $"{coin}:{netCode}".ToUpperInvariant(),
                            Ticker: coin.ToUpperInvariant(),
                            Network: NormalizeNetwork(netName, netCode)
                        ));
                    }
                }
                else
                {
                    // fallback
                    list.Add(new ExchangeCurrency(
                        ExchangeId: $"{coin}:{coin}".ToUpperInvariant(),
                        Ticker: coin.ToUpperInvariant(),
                        Network: "Mainnet"
                    ));
                }
            }

            return list
                .OrderBy(x => x.Ticker)
                .ThenBy(x => x.Network)
                .ToList();
        }
        catch
        {
            return Array.Empty<ExchangeCurrency>();
        }
    }

    // =========================
    // INTERNAL: /v1/info and /v1/info-revert
    // =========================
    private async Task<InfoResult?> PostInfoAsync(
        bool isRevert,
        string from,
        string to,
        string networkFrom,
        string networkTo,
        decimal amount,
        CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        var path = isRevert ? "v1/info-revert" : "v1/info";

        var body = new InfoRequest
        {
            From = from,
            To = to,
            NetworkFrom = networkFrom,
            NetworkTo = networkTo,
            Amount = amount,
            AffiliateId = opt.AffiliateId,
            Float = opt.UseFloatRate
        };

        var json = JsonSerializer.Serialize(body, JsonOpt);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var res = await http.SendForStringWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path);
            AddAuth(req);
            req.Content = content; // NOTE: StringContent can be re-used safely for same payload
            return req;
        }, timeout, opt.RetryCount, ct);

        if (res is null) return null;
        if (res.Status < HttpStatusCode.OK || res.Status >= HttpStatusCode.MultipleChoices) return null;

        var dto = JsonSerializer.Deserialize<InfoResponse>(res.Body, JsonOpt);
        if (dto is null) return null;

        var min = ParseDec(dto.MinAmount);
        var max = ParseDec(dto.MaxAmount);
        var amtOut = ParseDec(dto.Amount);

        return new InfoResult(min, max, amtOut);
    }

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(opt.UserAgent) && req.Headers.UserAgent.Count == 0)
            req.Headers.UserAgent.ParseAdd(opt.UserAgent);

        // Bearer token per their OpenAPI
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
    }

    private bool IsConfigured()
        => !string.IsNullOrWhiteSpace(opt.ApiKey) && !string.IsNullOrWhiteSpace(opt.AffiliateId);

    // =========================
    // COIN/NETWORK RESOLUTION
    // =========================
    private static (string coin, string network) ResolveCoinAndNetwork(AssetRef asset)
    {
        // If ExchangeId is like "USDT:TRC20" use it
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId))
        {
            var ex = asset.ExchangeId.Trim();

            var parts = ex.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                return (parts[0].ToUpperInvariant(), parts[1].ToUpperInvariant());

            // if someone passed "ETH-BEP20"
            var dash = ex.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (dash.Length == 2 && dash[0].Length >= 2)
                return (dash[0].ToUpperInvariant(), dash[1].ToUpperInvariant());
        }

        var coin = (asset.Ticker ?? "").Trim().ToUpperInvariant();
        if (coin.StartsWith("USDT", StringComparison.OrdinalIgnoreCase)) coin = "USDT";
        if (coin.StartsWith("USDC", StringComparison.OrdinalIgnoreCase)) coin = "USDC";

        var net = MapNetworkToLetsExchange(asset.Network, coin);
        return (coin, net);
    }

    private static string MapNetworkToLetsExchange(string? network, string coin)
    {
        // LetsExchange wants network codes like TRC20, ERC20, BEP20, etc.
        // For native coins, a safe default is: network code == coin code.
        if (string.IsNullOrWhiteSpace(network) || network.Equals("Mainnet", StringComparison.OrdinalIgnoreCase))
            return coin;

        return network.Trim() switch
        {
            "Tron" => "TRC20",
            "Ethereum" => "ERC20",
            "Binance Smart Chain" => "BEP20",
            "Arbitrum" => "ARBITRUM",
            "Base" => "BASE",
            "Polygon" => "POLYGON",
            "Solana" => "SOL",
            "Avalanche C-Chain" => "AVAXC",
            _ => coin
        };
    }

    private static string NormalizeNetwork(string? networkName, string? networkCode)
    {
        var code = (networkCode ?? "").Trim().ToUpperInvariant();
        if (code == "TRC20") return "Tron";
        if (code == "ERC20") return "Ethereum";
        if (code == "BEP20") return "Binance Smart Chain";
        if (code == "AVAXC") return "Avalanche C-Chain";
        if (code == "POLYGON") return "Polygon";
        if (code == "SOL") return "Solana";
        if (code == "BASE") return "Base";
        if (code == "ARBITRUM") return "Arbitrum";

        if (!string.IsNullOrWhiteSpace(networkName))
            return networkName.Trim();

        return "Mainnet";
    }

    private static decimal ParseDec(string? s)
        => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static decimal RoundUp(decimal v)
        => v <= 0 ? 0 : Math.Round(v, 8, MidpointRounding.AwayFromZero);

    private sealed record InfoResult(decimal MinAmount, decimal MaxAmount, decimal Amount);

    private sealed class InfoRequest
    {
        [JsonPropertyName("from")]
        public string From { get; set; } = "";

        [JsonPropertyName("to")]
        public string To { get; set; } = "";

        [JsonPropertyName("network_from")]
        public string NetworkFrom { get; set; } = "";

        [JsonPropertyName("network_to")]
        public string NetworkTo { get; set; } = "";

        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        [JsonPropertyName("affiliate_id")]
        public string AffiliateId { get; set; } = "";

        [JsonPropertyName("float")]
        public bool? Float { get; set; }
    }

    private sealed class InfoResponse
    {
        [JsonPropertyName("min_amount")]
        public string? MinAmount { get; set; }

        [JsonPropertyName("max_amount")]
        public string? MaxAmount { get; set; }

        // For /v1/info => final amount to RECEIVE
        // For /v1/info-revert => amount to SEND
        [JsonPropertyName("amount")]
        public string? Amount { get; set; }

        [JsonPropertyName("rate")]
        public string? Rate { get; set; }

        [JsonPropertyName("withdrawal_fee")]
        public string? WithdrawalFee { get; set; }
    }
}