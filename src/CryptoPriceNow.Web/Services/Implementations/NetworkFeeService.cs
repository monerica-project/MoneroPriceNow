using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CryptoPriceNow.Data.Interfaces;
using CryptoPriceNow.Data.Models;
using CryptoPriceNow.Web.Models;
using ExchangeServices.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CryptoPriceNow.Services;

/// <summary>
/// Fetches current on-chain fees from free public sources:
///   • Bitcoin  — mempool.space recommended fees (sat/vB)
///   • Ethereum — JSON-RPC eth_feeHistory (base fee + priority tip, in Gwei)
///   • Monero   — monerod get_fee_estimate (per-byte fees)
/// Results are normalized to <see cref="NetworkFee"/> and cached in a snapshot
/// dictionary that the warmer refreshes; reads are lock-free and instant.
/// </summary>
public sealed class NetworkFeeService : INetworkFeeService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IHttpClientFactory _httpFactory;
    private readonly NetworkFeeOptions _opt;
    private readonly ILogger<NetworkFeeService> _log;
    private readonly INetworkFeeQuoteSink _sink;

    private readonly ConcurrentDictionary<string, NetworkFee> _snapshot =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    // Short-lived USD spot price cache (BTC/ETH/XMR), shared across networks.
    private static readonly TimeSpan UsdTtl = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim _usdLock = new(1, 1);
    private Dictionary<string, decimal> _usdPrices = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _usdAt;

    public IReadOnlyList<string> Networks { get; } = ["bitcoin", "ethereum", "monero"];

    public NetworkFeeService(
        IHttpClientFactory httpFactory,
        IOptions<NetworkFeeOptions> options,
        ILogger<NetworkFeeService> log,
        INetworkFeeQuoteSink sink)
    {
        _httpFactory = httpFactory;
        _opt = options.Value;
        _log = log;
        _sink = sink;
    }

    public async Task<NetworkFee?> GetFeeAsync(string network, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(network)) return null;
        if (_snapshot.TryGetValue(network, out var cached)) return cached;

        // Cold cache (e.g. first hit before the warmer finished) — fetch once.
        await RefreshAsync(network, ct);
        return _snapshot.TryGetValue(network, out var v) ? v : null;
    }

    public async Task RefreshAsync(string network, CancellationToken ct = default)
    {
        if (!_opt.Enabled || string.IsNullOrWhiteSpace(network)) return;

        var gate = _locks.GetOrAdd(network, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var fee = network.Trim().ToLowerInvariant() switch
            {
                "bitcoin"  => await FetchBitcoinAsync(ct),
                "ethereum" => await FetchEthereumAsync(ct),
                "monero"   => await FetchMoneroAsync(ct),
                _ => null
            };
            if (fee is not null)
            {
                _snapshot[network] = fee;

                // Log the representative sample for the time-series chart (fire-and-forget).
                if (fee.Sample is { } s)
                {
                    try
                    {
                        await _sink.EnqueueAsync(
                            new NetworkFeeSnapshot(fee.Network, s.Native, s.NativeUnit, s.UsdPerTx, fee.UpdatedAtUtc),
                            ct);
                    }
                    catch { /* logging must never break fee serving */ }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[NetworkFee] refresh failed for {Network}", network);
        }
        finally { gate.Release(); }
    }

    // ── Bitcoin ──────────────────────────────────────────────────────────────
    private async Task<NetworkFee?> FetchBitcoinAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _opt.BitcoinFeesUrl);
        AddUserAgent(req);
        var res = await SendAsync(req, ct);
        if (!Ok(res)) return null;

        using var doc = JsonDocument.Parse(res!.Body);
        var r = doc.RootElement;
        int fastest = r.GetProperty("fastestFee").GetInt32();
        int half    = r.GetProperty("halfHourFee").GetInt32();
        int hour    = r.GetProperty("hourFee").GetInt32();

        var usd = await GetUsdAsync("bitcoin", ct);
        int vb = Math.Max(1, _opt.BitcoinTypicalVBytes);
        NetworkFeeTier Tier(string label, string eta, int satPerVb)
        {
            long sats = (long)satPerVb * vb;
            decimal btc = (decimal)sats / 100_000_000m;
            return BuildTier(label, $"{satPerVb} sat/vB", eta,
                usd is decimal p ? btc * p : (decimal?)null);
        }

        var (tiers, allSame) = CheckAllSame(
        [
            Tier("Fast",   "~10 min", fastest),
            Tier("Normal", "~30 min", half),
            Tier("Slow",   "~1 hr",   hour),
        ]);

        var note = $"Cost shown is for a typical ~{vb} vByte transaction. Source: mempool.space.";
        if (allSame) note = "Network is uncongested — all priorities cost the same right now. " + note;

        // Representative "Normal" sample for the time-series (half-hour fee).
        decimal normalBtc = (decimal)((long)half * vb) / 100_000_000m;
        var sample = new NetworkFeeSample(half, "sat/vB", usd is decimal up ? normalBtc * up : (decimal?)null);

        return new NetworkFee("bitcoin", "Bitcoin Network Fee", tiers, note, DateTimeOffset.UtcNow, sample);
    }

    // ── Ethereum ─────────────────────────────────────────────────────────────
    private async Task<NetworkFee?> FetchEthereumAsync(CancellationToken ct)
    {
        // One call gives the next-block base fee + priority tips at 3 percentiles.
        const string body =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_feeHistory\",\"params\":[\"0x5\",\"latest\",[10,50,90]]}";
        var root = await RpcAsync(_opt.EthereumRpcUrl, body, ct);
        if (root is null) return await FetchEthereumGasPriceAsync(ct);

        try
        {
            var result = root.Value.GetProperty("result");
            var baseArr = result.GetProperty("baseFeePerGas");
            var nextBase = HexToBig(baseArr[baseArr.GetArrayLength() - 1].GetString());

            var reward = result.GetProperty("reward");
            int n = reward.GetArrayLength();
            if (n == 0) return await FetchEthereumGasPriceAsync(ct);

            var tip = new BigInteger[3];
            foreach (var block in reward.EnumerateArray())
                for (int i = 0; i < 3; i++)
                    tip[i] += HexToBig(block[i].GetString());
            for (int i = 0; i < 3; i++) tip[i] /= n;

            var usd = await GetUsdAsync("ethereum", ct);
            NetworkFeeTier Tier(string label, string eta, BigInteger priorityTip)
            {
                var total = nextBase + priorityTip;
                var feeWei = total * 21000;
                decimal eth = (decimal)((double)feeWei / 1e18);
                return BuildTier(label, $"{FormatGwei(total)} Gwei", eta,
                    usd is decimal p ? eth * p : (decimal?)null);
            }

            var (tiers, _) = CheckAllSame(
            [
                Tier("Fast",   "~15 sec",  tip[2]),
                Tier("Normal", "~30 sec",  tip[1]),
                Tier("Slow",   "~1 min+",  tip[0]),
            ]);

            // Representative "Normal" sample (median priority).
            var normalTotal = nextBase + tip[1];
            decimal normalGwei = (decimal)((double)normalTotal / 1e9);
            decimal normalEth = (decimal)((double)(normalTotal * 21000) / 1e18);
            var sample = new NetworkFeeSample(normalGwei, "Gwei",
                usd is decimal up ? normalEth * up : (decimal?)null);

            return new NetworkFee("ethereum", "Ethereum Gas Fee", tiers,
                $"Cost shown is for a standard transfer (21,000 gas). Base fee {FormatGwei(nextBase)} Gwei.",
                DateTimeOffset.UtcNow, sample);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[NetworkFee] eth_feeHistory parse failed, falling back to eth_gasPrice");
            return await FetchEthereumGasPriceAsync(ct);
        }
    }

    private async Task<NetworkFee?> FetchEthereumGasPriceAsync(CancellationToken ct)
    {
        const string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_gasPrice\",\"params\":[]}";
        var root = await RpcAsync(_opt.EthereumRpcUrl, body, ct);
        if (root is null) return null;
        try
        {
            var gp = HexToBig(root.Value.GetProperty("result").GetString());
            var usd = await GetUsdAsync("ethereum", ct);
            var feeWei = gp * 21000;
            decimal eth = (decimal)((double)feeWei / 1e18);
            var tiers = new[]
            {
                BuildTier("Current", $"{FormatGwei(gp)} Gwei", "transfer",
                    usd is decimal p ? eth * p : (decimal?)null),
            };
            var sample = new NetworkFeeSample((decimal)((double)gp / 1e9), "Gwei",
                usd is decimal up ? eth * up : (decimal?)null);
            return new NetworkFee("ethereum", "Ethereum Gas Fee", tiers,
                "A plain transfer costs 21,000 gas.", DateTimeOffset.UtcNow, sample);
        }
        catch { return null; }
    }

    // ── Monero ───────────────────────────────────────────────────────────────
    private async Task<NetworkFee?> FetchMoneroAsync(CancellationToken ct)
    {
        var url = _opt.MoneroNodeUrl.TrimEnd('/') + "/json_rpc";
        const string body = "{\"jsonrpc\":\"2.0\",\"id\":\"0\",\"method\":\"get_fee_estimate\"}";
        var root = await RpcAsync(url, body, ct);
        if (root is null) return null;

        try
        {
            var result = root.Value.GetProperty("result");

            long[] perByte;
            if (result.TryGetProperty("fees", out var feesEl)
                && feesEl.ValueKind == JsonValueKind.Array && feesEl.GetArrayLength() > 0)
            {
                perByte = feesEl.EnumerateArray().Select(e => e.GetInt64()).ToArray();
            }
            else
            {
                perByte = [result.GetProperty("fee").GetInt64()];
            }

            var usd = await GetUsdAsync("monero", ct);
            int weight = Math.Max(1, _opt.MoneroTypicalTxBytes);
            string[] labels = ["Slow", "Normal", "Fast", "Fastest"];
            string[] etas   = ["~20 min+", "~10 min", "~4 min", "next block"];

            var built = new List<NetworkFeeTier>();
            int count = Math.Min(perByte.Length, 4);
            for (int i = 0; i < count; i++)
            {
                // per-byte fee is in atomic units (piconero, 1 XMR = 1e12).
                decimal atomic = (decimal)perByte[i] * weight;
                decimal xmr = atomic / 1_000_000_000_000m;
                string label = perByte.Length == 1 ? "Normal" : labels[i];
                string eta   = perByte.Length == 1 ? "typical tx" : etas[i];
                built.Add(BuildTier(label, $"{FormatXmr(atomic)}", eta,
                    usd is decimal p ? xmr * p : (decimal?)null));
            }

            var (tiers, _) = CheckAllSame(built);

            // Representative "Normal" sample (fees[1] when present, else the single fee).
            int normalIdx = perByte.Length >= 2 ? 1 : 0;
            decimal normalXmr = (decimal)perByte[normalIdx] * weight / 1_000_000_000_000m;
            var sample = new NetworkFeeSample(perByte[normalIdx], "pXMR/byte",
                usd is decimal up ? normalXmr * up : (decimal?)null);

            return new NetworkFee("monero", "Monero Network Fee", tiers,
                $"Estimated for a ~{weight}-byte transaction (~2 in / 2 out). Monero fees scale with tx size.",
                DateTimeOffset.UtcNow, sample);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[NetworkFee] monero get_fee_estimate parse failed");
            return null;
        }
    }

    // ── HTTP / JSON-RPC plumbing ─────────────────────────────────────────────
    private async Task<JsonElement?> RpcAsync(string url, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AddUserAgent(req);
        var res = await SendAsync(req, ct);
        if (!Ok(res)) return null;
        try
        {
            using var doc = JsonDocument.Parse(res!.Body);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Latest USD spot price for "bitcoin"/"ethereum"/"monero", cached briefly.</summary>
    private async Task<decimal?> GetUsdAsync(string coinId, CancellationToken ct)
    {
        if (_usdPrices.TryGetValue(coinId, out var hit) && DateTimeOffset.UtcNow - _usdAt < UsdTtl)
            return hit;

        await _usdLock.WaitAsync(ct);
        try
        {
            if (_usdPrices.TryGetValue(coinId, out var hit2) && DateTimeOffset.UtcNow - _usdAt < UsdTtl)
                return hit2;

            using var req = new HttpRequestMessage(HttpMethod.Get, _opt.UsdPriceUrl);
            AddUserAgent(req);
            var res = await SendAsync(req, ct);
            if (Ok(res))
            {
                using var doc = JsonDocument.Parse(res!.Body);
                var root = doc.RootElement;
                var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var coin in (string[])["bitcoin", "ethereum", "monero"])
                {
                    if (root.TryGetProperty(coin, out var c)
                        && c.TryGetProperty("usd", out var u)
                        && u.TryGetDecimal(out var val))
                        dict[coin] = val;
                }
                if (dict.Count > 0) { _usdPrices = dict; _usdAt = DateTimeOffset.UtcNow; }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[NetworkFee] USD price fetch failed"); }
        finally { _usdLock.Release(); }

        return _usdPrices.TryGetValue(coinId, out var v) ? v : (decimal?)null;
    }

    /// <summary>
    /// Always keeps all tiers (so the standard Fast/Normal/Slow set is shown), and
    /// reports whether they currently all share the same headline rate — which
    /// happens in a quiet mempool and is worth explaining rather than hiding.
    /// </summary>
    private static (IReadOnlyList<NetworkFeeTier> Tiers, bool AllSame) CheckAllSame(
        IReadOnlyList<NetworkFeeTier> tiers)
    {
        var distinct = tiers.Select(t => t.Primary)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return (tiers, distinct <= 1 && tiers.Count > 1);
    }

    private Task<HttpStringResponse?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_opt.TimeoutSeconds, 2, 30));
        return http.SendForStringWithTimeoutAsync(req, timeout, ct);
    }

    private void AddUserAgent(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_opt.UserAgent))
            req.Headers.UserAgent.ParseAdd(_opt.UserAgent);
    }

    private static bool Ok(HttpStringResponse? res) =>
        res is not null
        && (int)res.StatusCode >= 200 && (int)res.StatusCode < 300
        && !string.IsNullOrWhiteSpace(res.Body);

    // ── number formatting ────────────────────────────────────────────────────
    private static BigInteger HexToBig(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return BigInteger.Zero;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length == 0) return BigInteger.Zero;
        // Prefix "0" so the high bit is never read as a sign.
        return BigInteger.Parse("0" + hex, NumberStyles.HexNumber, Inv);
    }

    private static string FormatGwei(BigInteger wei)
    {
        double g = (double)wei / 1e9;
        return g >= 10 ? g.ToString("0.0", Inv) : g.ToString("0.###", Inv);
    }

    private static string FormatXmr(decimal atomic) => Trim((double)atomic / 1e12, 8) + " XMR";

    private static string Trim(double v, int dp)
    {
        var s = v.ToString("F" + dp, Inv);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s.Length == 0 ? "0" : s;
    }

    /// <summary>
    /// Builds a tier with the USD cost of a typical transaction as the headline —
    /// the number that's comparable across coins — and the native rate + ETA as the
    /// supporting line. Falls back to the native rate as the headline if no USD price.
    /// </summary>
    private static NetworkFeeTier BuildTier(string label, string nativeRate, string eta, decimal? usdValue)
    {
        var usd = FormatUsd(usdValue);
        return usd is not null
            ? new NetworkFeeTier(label, usd, $"{nativeRate} · {eta}")
            : new NetworkFeeTier(label, nativeRate, eta);
    }

    /// <summary>Formats a USD amount: cents at/above 1¢, more precision when sub-cent.</summary>
    private static string? FormatUsd(decimal? usd)
    {
        if (usd is not decimal v || v <= 0) return null;
        if (v >= 0.01m) return "$" + v.ToString("0.00", Inv);
        var s = v.ToString("0.000000", Inv).TrimEnd('0').TrimEnd('.');
        return s is "0" or "" ? "<$0.000001" : "$" + s;
    }
}
