using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

public sealed class StealthExClient : IStealthExClient
{
    private readonly HttpClient http;
    private readonly StealthExOptions opt;

    public string  ExchangeKey => "stealthex";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;

    public StealthExClient(HttpClient http, IOptions<StealthExOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        // SELL: send 1 XMR → get X USDT
        // direct + floating, amount = 1 XMR
        var fromSymbol = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToLowerInvariant();
        var toSymbol = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToLowerInvariant();
        var fromNetwork = ToStealthExNetwork(query.Base.Ticker, query.Base.Network);

        var toNetworkPrimary = ToStealthExNetwork(query.Quote.Ticker, query.Quote.Network);
        var toNetworksToTry = BuildTronNetworkList(query.Quote.Network, toNetworkPrimary);

        foreach (var toNetwork in toNetworksToTry)
        {
            var result = await PostEstimatedAmountAsync(
                fromSymbol, fromNetwork,
                toSymbol, toNetwork,
                estimation: "direct",
                rate: "floating",
                amount: 1m,
                ct);

            if (result is null) continue;

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: result.Value.EstimatedAmount, // USDT received per 1 XMR sold
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: result.Value.CorrelationId,
                Raw: null);
        }

        return null;
    }
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return null;

        // BUY: send X USDT → receive Y XMR (direct + floating)
        // We use a realistic amount (500 USDT) because StealthEX rejects amounts
        // below its minimum (~10-20 USDT). We then calculate: 500 / Y = USDT per XMR.
        const decimal probeAmount = 500m;

        var fromSymbol = (query.Quote.ExchangeId ?? query.Quote.Ticker).Trim().ToLowerInvariant();
        var toSymbol = (query.Base.ExchangeId ?? query.Base.Ticker).Trim().ToLowerInvariant();
        var toNetwork = ToStealthExNetwork(query.Base.Ticker, query.Base.Network);

        var fromNetworkPrimary = ToStealthExNetwork(query.Quote.Ticker, query.Quote.Network);
        var fromNetworksToTry = BuildTronNetworkList(query.Quote.Network, fromNetworkPrimary);

        foreach (var fromNetwork in fromNetworksToTry)
        {
            var result = await PostEstimatedAmountAsync(
                fromSymbol, fromNetwork,
                toSymbol, toNetwork,
                estimation: "direct",
                rate: "floating",
                amount: probeAmount,  // ← send 500 USDT, get back Y XMR
                ct);

            if (result is null || result.Value.EstimatedAmount <= 0) continue;

            // 500 USDT buys result.EstimatedAmount XMR
            // → USDT per 1 XMR = 500 / result.EstimatedAmount
            var usdtPerXmr = probeAmount / result.Value.EstimatedAmount;

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: usdtPerXmr,
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: result.Value.CorrelationId,
                Raw: null);
        }

        return null;
    }
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
            return Array.Empty<ExchangeCurrency>();

        var results = new List<ExchangeCurrency>();
        var limit = Math.Clamp(opt.CurrenciesPageSize, 25, 250);

        for (int offset = 0; ; offset += limit)
        {
            var url = $"/v4/currencies?limit={limit}&offset={offset}";
            var tries = 0;

            while (true)
            {
                tries++;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.CurrenciesTimeoutSeconds, 5, 180)));

                HttpResponseMessage? resp = null;

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);

                    resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                    var raw = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

                    if (!resp.IsSuccessStatusCode)
                        return results.Count == 0 ? Array.Empty<ExchangeCurrency>() : results;

                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return results.Count == 0 ? Array.Empty<ExchangeCurrency>() : results;

                    var arr = doc.RootElement.EnumerateArray().ToList();

                    foreach (var item in arr)
                    {
                        var symbol = item.TryGetProperty("symbol", out var sEl) ? sEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(symbol)) continue;

                        string network = "mainnet";
                        if (item.TryGetProperty("network", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                            network = nEl.GetString() ?? "mainnet";

                        results.Add(new ExchangeCurrency(
                            ExchangeId: symbol.ToLowerInvariant(),
                            Ticker: symbol.ToUpperInvariant(),
                            Network: FromStealthExNetwork(network)));
                    }

                    if (arr.Count < limit)
                        return results;

                    break; // next page
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (tries <= Math.Clamp(opt.CurrenciesTimeoutRetries, 0, 3))
                        continue;

                    return results.Count == 0 ? Array.Empty<ExchangeCurrency>() : results;
                }
                catch (HttpRequestException)
                {
                    return results.Count == 0 ? Array.Empty<ExchangeCurrency>() : results;
                }
                finally
                {
                    resp?.Dispose();
                }
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<(decimal EstimatedAmount, string? CorrelationId)?> PostEstimatedAmountAsync(
        string fromSymbol, string fromNetwork,
        string toSymbol, string toNetwork,
        string estimation, string rate,
        decimal amount,
        CancellationToken ct)
    {
        var bodyObj = new
        {
            route = new
            {
                from = new { symbol = fromSymbol, network = fromNetwork },
                to = new { symbol = toSymbol, network = toNetwork }
            },
            estimation,
            rate,
            amount
        };

        var json = JsonSerializer.Serialize(bodyObj);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v4/rates/estimated-amount");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            return null;

        var dto = JsonSerializer.Deserialize<EstimatedAmountResponse>(
            raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto is null || dto.EstimatedAmount <= 0)
            return null;

        var correlationId = resp.Headers.TryGetValues("Request-Id", out var vals)
            ? vals.FirstOrDefault()
            : null;

        return (dto.EstimatedAmount, correlationId);
    }

    /// <summary>
    /// For Tron-based tokens, returns multiple network codes to try in order.
    /// For everything else, returns just the primary.
    /// </summary>
    private static string[] BuildTronNetworkList(string? network, string primary)
    {
        return (network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) ?? false)
            ? new[] { primary, "tron", "trx", "trc20" }.Distinct().ToArray()
            : new[] { primary };
    }

    private static string ToStealthExNetwork(string ticker, string? network)
    {
        if (string.IsNullOrWhiteSpace(network))
            return "mainnet";

        return network.Trim() switch
        {
            "Mainnet" => "mainnet",
            "Ethereum" => "eth",
            "Binance Smart Chain" => "bsc",
            "Solana" => "sol",
            "Tron" => "trx",
            _ => network.Trim().ToLowerInvariant()
        };
    }

    private static string FromStealthExNetwork(string network)
    {
        return network.Trim().ToLowerInvariant() switch
        {
            "mainnet" => "Mainnet",
            "eth" => "Ethereum",
            "bsc" => "Binance Smart Chain",
            "sol" => "Solana",
            "tron" => "Tron",
            "trx" => "Tron",
            _ => network
        };
    }

    private sealed class EstimatedAmountResponse
    {
        [JsonPropertyName("estimated_amount")]
        public decimal EstimatedAmount { get; set; }
    }
}