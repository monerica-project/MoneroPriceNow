namespace CryptoPriceNow.Web.Models;

/// <summary>
/// Config for the on-chain fee widget. Bound from the "NetworkFee" section.
/// Every value has a sensible free default, so the feature works with no config
/// at all. See appsettings_template.json for paid-provider alternatives.
/// </summary>
public sealed class NetworkFeeOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often the background warmer refreshes each network (seconds).</summary>
    public int WarmIntervalSeconds { get; set; } = 60;

    /// <summary>Per-request HTTP timeout (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 8;

    public string UserAgent { get; set; } = "MoneroPriceNow/1.0 (+https://moneropricenow.com)";

    /// <summary>Free USD spot prices for BTC/ETH/XMR (CoinGecko, no key) — used to show fees in dollars.</summary>
    public string UsdPriceUrl { get; set; } =
        "https://api.coingecko.com/api/v3/simple/price?ids=bitcoin,ethereum,monero&vs_currencies=usd";

    // ── Bitcoin ──────────────────────────────────────────────────────────────
    // mempool.space recommended fees — free, no API key.
    public string BitcoinFeesUrl { get; set; } = "https://mempool.space/api/v1/fees/recommended";
    /// <summary>Size of a "typical" BTC tx used to estimate a per-tx fee from sat/vB.</summary>
    public int BitcoinTypicalVBytes { get; set; } = 140; // 1-in / 2-out native segwit

    // ── Ethereum ─────────────────────────────────────────────────────────────
    // Any JSON-RPC endpoint. The public default needs no key; for higher rate
    // limits / reliability point this at Alchemy/Infura/your own node.
    public string EthereumRpcUrl { get; set; } = "https://ethereum-rpc.publicnode.com";

    // ── Monero ───────────────────────────────────────────────────────────────
    // Any monerod restricted RPC (get_fee_estimate is a public method). Running
    // your own node is best for privacy/reliability.
    public string MoneroNodeUrl { get; set; } = "https://xmr-node.cakewallet.com:18081";
    /// <summary>Size of a "typical" XMR tx used to estimate a per-tx fee from per-byte fees.</summary>
    public int MoneroTypicalTxBytes { get; set; } = 1500; // ~2-in / 2-out
}
