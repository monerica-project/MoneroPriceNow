using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class FuguSwapClient : IFuguSwapClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient http;
    private readonly FuguSwapOptions opt;

    public string  ExchangeKey => "fuguswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;

    public FuguSwapClient(HttpClient http, IOptions<FuguSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> ? USDT
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        // from = XMR, to = USDT, with Tron on quote
        var (fromCoin, fromNet) = ResolveCoinAndNetwork(query.Base);
        var (toCoin, toNet) = ResolveCoinAndNetwork(query.Quote);

        if (string.IsNullOrWhiteSpace(fromCoin) || string.IsNullOrWhiteSpace(toCoin))
            return null;

        // Try a few Tron network names if quote is Tron
        var toNetworkCandidates = NetworkCandidates(query.Quote, toNet);

        foreach (var net in toNetworkCandidates)
        {
            var dto = await GetPriceDtoAsync(
                type: "float",
                amount: 1m,
                from: fromCoin,
                to: toCoin,
                fromNetwork: NormalizeOptionalNetwork(fromNet),
                toNetwork: NormalizeOptionalNetwork(net),
                ct);

            if (dto is null || dto.AmountTo <= 0)
                continue;

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: dto.AmountTo, // USDT per 1 XMR
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: null,
                Raw: null
            );
        }

        return null;
    }

    // ==========================================
    // BUY: ? USDT needed to receive ~1 XMR
    // ==========================================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        // BUY means: pay USDT (Tron) to receive XMR
        var (fromCoin, fromNet) = ResolveCoinAndNetwork(query.Quote); // USDT + Tron
        var (toCoin, toNet) = ResolveCoinAndNetwork(query.Base);      // XMR

        if (string.IsNullOrWhiteSpace(fromCoin) || string.IsNullOrWhiteSpace(toCoin))
            return null;

        var fromNetworkCandidates = NetworkCandidates(query.Quote, fromNet);

        foreach (var fromNetwork in fromNetworkCandidates)
        {
            // Step 1: find any valid quote with a sane USDT amount
            var seedAmounts = new[] { 25m, 50m, 100m, 200m, 500m, 1000m };
            PriceDto? first = null;
            decimal usedFrom = 0m;

            foreach (var amt in seedAmounts)
            {
                var dto = await GetPriceDtoAsync(
                    type: "float",
                    amount: amt,
                    from: fromCoin,
                    to: toCoin,
                    fromNetwork: NormalizeOptionalNetwork(fromNetwork),
                    toNetwork: NormalizeOptionalNetwork(toNet),
                    ct);

                if (dto is null)
                    continue;

                // If they return min_deposit and we're below it, try min_deposit once
                if (dto.MinDeposit > 0 && amt < dto.MinDeposit)
                {
                    var dtoMin = await GetPriceDtoAsync(
                        type: "float",
                        amount: dto.MinDeposit,
                        from: fromCoin,
                        to: toCoin,
                        fromNetwork: NormalizeOptionalNetwork(fromNetwork),
                        toNetwork: NormalizeOptionalNetwork(toNet),
                        ct);

                    if (dtoMin is not null && dtoMin.AmountTo > 0)
                    {
                        first = dtoMin;
                        usedFrom = dto.MinDeposit;
                        break;
                    }

                    continue;
                }

                if (dto.AmountTo > 0)
                {
                    first = dto;
                    usedFrom = amt;
                    break;
                }
            }

            if (first is null || first.AmountTo <= 0 || usedFrom <= 0)
                continue;

            // implied USDT per 1 XMR
            var p1 = SafeDiv(usedFrom, first.AmountTo);
            if (p1 <= 0)
                continue;

            // Step 2: refine once using the implied amount needed for ~1 XMR
            var refine = Clamp(p1, 10m, 5000m);

            // respect min_deposit if provided
            if (first.MinDeposit > 0 && refine < first.MinDeposit)
                refine = first.MinDeposit;

            var second = await GetPriceDtoAsync(
                type: "float",
                amount: refine,
                from: fromCoin,
                to: toCoin,
                fromNetwork: NormalizeOptionalNetwork(fromNetwork),
                toNetwork: NormalizeOptionalNetwork(toNet),
                ct);

            if (second is null || second.AmountTo <= 0)
                continue;

            var p2 = SafeDiv(refine, second.AmountTo);
            if (p2 <= 0)
                continue;

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: p2, // USDT needed per 1 XMR (approx)
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: null,
                Raw: null
            );
        }

        return null;
    }

    // =========================
    // CURRENCIES
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return Array.Empty<ExchangeCurrency>();

        var results = new List<ExchangeCurrency>();

        // Endpoint is paginated: page (default 1), size (default 50)
        const int size = 100;

        for (int page = 1; page <= 200; page++)
        {
            var url = $"/api/cryptos/networks?page={page}&size={size}";

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuth(req);

                using var resp = await http.SendAsync(req, ct);
                var raw = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    break;

                var dto = JsonSerializer.Deserialize<CryptosNetworksResponse>(raw, JsonOpt);
                if (dto?.Items is null || dto.Items.Count == 0)
                    break;

                foreach (var item in dto.Items)
                {
                    var sym = (item.ShortName ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(sym))
                        continue;

                    // If networks present, create one entry per network
                    if (item.Networks is { Count: > 0 })
                    {
                        foreach (var n in item.Networks)
                        {
                            var nName = (n.Name ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(nName))
                                continue;

                            // Store ExchangeId as "COIN|NETWORK" so we can recover exact network later
                            results.Add(new ExchangeCurrency(
                                ExchangeId: $"{sym.ToUpperInvariant()}|{nName}",
                                Ticker: sym.ToUpperInvariant(),
                                Network: NormalizeNetworkForUi(nName)
                            ));
                        }
                    }
                    else
                    {
                        results.Add(new ExchangeCurrency(
                            ExchangeId: sym.ToUpperInvariant(),
                            Ticker: sym.ToUpperInvariant(),
                            Network: "Mainnet"
                        ));
                    }
                }

                // stop when last page
                if (dto.Pages > 0 && page >= dto.Pages)
                    break;

                if (dto.Items.Count < size)
                    break;
            }
            catch (TaskCanceledException)
            {
                return Array.Empty<ExchangeCurrency>();
            }
            catch (HttpRequestException)
            {
                return Array.Empty<ExchangeCurrency>();
            }
        }

        return results
            .GroupBy(x => (x.ExchangeId, x.Ticker, x.Network), StringTupleComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ticker)
            .ThenBy(x => x.Network)
            .ToList();
    }

    // =========================
    // INTERNAL: /api/price
    // =========================
    private async Task<PriceDto?> GetPriceDtoAsync(
        string type,
        decimal amount,
        string from,
        string to,
        string? fromNetwork,
        string? toNetwork,
        CancellationToken ct)
    {
        if (amount <= 0) return null;

        var qs =
            $"type={Uri.EscapeDataString(type)}&" +
            $"amount={Uri.EscapeDataString(amount.ToString(CultureInfo.InvariantCulture))}&" +
            $"from={Uri.EscapeDataString(from)}&" +
            $"to={Uri.EscapeDataString(to)}";

        if (!string.IsNullOrWhiteSpace(fromNetwork))
            qs += $"&from_network={Uri.EscapeDataString(fromNetwork)}";

        if (!string.IsNullOrWhiteSpace(toNetwork))
            qs += $"&to_network={Uri.EscapeDataString(toNetwork)}";

        var url = $"/api/price?{qs}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuth(req);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await http.SendAsync(req, ct);
            var raw = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return null;

            return JsonSerializer.Deserialize<PriceDto>(raw, JsonOpt);
        }
        catch (TaskCanceledException)
        {
            // covers HttpClient.Timeout too
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void AddAuth(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("X-API-Key", opt.ApiKey);
    }

    // =========================
    // COIN + NETWORK RESOLUTION
    // =========================
    private static (string coin, string? network) ResolveCoinAndNetwork(AssetRef asset)
    {
        // If ExchangeId is "COIN|NETWORK", use it
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId) && asset.ExchangeId.Contains('|'))
        {
            var parts = asset.ExchangeId.Split('|', 2, StringSplitOptions.TrimEntries);
            var c = parts.Length > 0 ? parts[0] : "";
            var n = parts.Length > 1 ? parts[1] : null;

            if (!string.IsNullOrWhiteSpace(c))
                return (c, n);
        }

        // Else coin is ticker-ish
        var coin = (asset.ExchangeId ?? asset.Ticker ?? "").Trim();
        if (string.IsNullOrWhiteSpace(coin))
            return ("", null);

        // network from AssetRef.Network (UI value)
        return (coin.ToUpperInvariant(), MapUiNetworkToApiNetwork(asset.Network));
    }

    private static IEnumerable<string?> NetworkCandidates(AssetRef asset, string? primary)
    {
        // If ExchangeId carried an explicit network, don't expand
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId) && asset.ExchangeId.Contains('|'))
            return new[] { primary };

        // Expand Tron variants when UI says Tron
        if (asset.Network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) ?? false)
        {
            return new[] { primary, "TRC20", "TRX", "TRON", "tron", "trx", "trc20" }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return new[] { primary }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeOptionalNetwork(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return null;

        // if UI passed "Mainnet", omit it (API treats empty as default)
        if (n.Equals("Mainnet", StringComparison.OrdinalIgnoreCase))
            return null;

        return n.Trim();
    }

    private static string? MapUiNetworkToApiNetwork(string? ui)
    {
        if (string.IsNullOrWhiteSpace(ui)) return null;

        var n = ui.Trim().ToLowerInvariant();
        return n switch
        {
            "tron" => "TRC20",
            "ethereum" => "ERC20",
            "binance smart chain" => "BSC",
            "solana" => "SOL",
            "arbitrum" => "ARBITRUM",
            "base" => "BASE",
            "polygon" => "MATIC",
            "mainnet" => null,
            _ => ui.Trim()
        };
    }

    private static string NormalizeNetworkForUi(string apiNetworkName)
    {
        var n = apiNetworkName.Trim().ToLowerInvariant();
        if (n.Contains("trc") || n.Contains("trx") || n.Contains("tron"))
            return "Tron";
        if (n.Contains("erc") || n == "eth" || n.Contains("ethereum"))
            return "Ethereum";
        if (n.Contains("bsc"))
            return "Binance Smart Chain";
        if (n.Contains("sol"))
            return "Solana";
        if (n.Contains("arbitrum"))
            return "Arbitrum";
        if (n.Contains("base"))
            return "Base";
        if (n.Contains("matic") || n.Contains("polygon"))
            return "Polygon";

        // if it's literally the base chain name, treat as mainnet
        if (n is "mainnet" or "xmr" or "btc" or "ltc" or "doge")
            return "Mainnet";

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n);
    }

    private static decimal SafeDiv(decimal a, decimal b) => b == 0 ? 0 : a / b;
    private static decimal Clamp(decimal v, decimal min, decimal max) => v < min ? min : (v > max ? max : v);

    // =========================
    // DTOs
    // =========================
    private sealed class PriceDto
    {
        [JsonPropertyName("from")]
        public string? From { get; set; }

        [JsonPropertyName("to")]
        public string? To { get; set; }

        [JsonPropertyName("amount_from")]
        public decimal AmountFrom { get; set; }

        [JsonPropertyName("amount_to")]
        public decimal AmountTo { get; set; }

        [JsonPropertyName("min_deposit")]
        public decimal MinDeposit { get; set; }
    }

    private sealed class CryptosNetworksResponse
    {
        [JsonPropertyName("items")]
        public List<CryptoNetworkItem> Items { get; set; } = new();

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("pages")]
        public int Pages { get; set; }
    }

    private sealed class CryptoNetworkItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("short_name")]
        public string? ShortName { get; set; }

        [JsonPropertyName("is_memo")]
        public bool? IsMemo { get; set; }

        [JsonPropertyName("networks")]
        public List<NetworkItem> Networks { get; set; } = new();
    }

    private sealed class NetworkItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("default")]
        public bool? Default { get; set; }
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ExchangeId, string Ticker, string Network)>
    {
        public static readonly StringTupleComparer OrdinalIgnoreCase = new();

        public bool Equals((string ExchangeId, string Ticker, string Network) x, (string ExchangeId, string Ticker, string Network) y)
            => string.Equals(x.ExchangeId, y.ExchangeId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Ticker, y.Ticker, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Network, y.Network, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ExchangeId, string Ticker, string Network) obj)
            => HashCode.Combine(
                obj.ExchangeId?.ToLowerInvariant(),
                obj.Ticker?.ToLowerInvariant(),
                obj.Network?.ToLowerInvariant()
            );
    }
}