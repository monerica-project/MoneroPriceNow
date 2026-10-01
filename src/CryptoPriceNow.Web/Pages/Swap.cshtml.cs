using System.Globalization;
using CryptoPriceNow.Web.Models;
using CryptoPriceNow.Web.Support;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>
/// /swap — a live swap-rate finder built on the SAME exchange APIs that power the price board.
/// One side of every trade is always XMR; the other is BTC, ETH or USDT (Tron/TRC20). The user
/// enters an amount and a direction (⇄ reverses it), and we rank every exchange currently able
/// to process that direction by how much they'd receive.
///
/// Sourcing:
///  • asset → XMR (buying Monero): two-way board rows (Buy side), which already exclude venues
///    that can't quote both ways and are spread-sane.
///  • XMR → asset (selling Monero): one-way SELL rows, so sell-only venues (e.g. xmr2cex) appear.
/// Each exchange shows once (deduped, best rate kept). Monerica sponsors are highlighted and get
/// a clean direct link instead of the affiliate URL; everyone else links via their affiliate URL.
/// The exchange name links to that listing on Monerica.
/// </summary>
public sealed class SwapModel : PageModel
{
    private readonly SwapQuoteService swap;

    public SwapModel(SwapQuoteService swap)
    {
        this.swap = swap;
    }

    /// <summary>The non-XMR assets a trade can be paired against.</summary>
    public static readonly string[] Assets = { "btc", "eth", "usdt" };

    private static decimal DefaultAmountFor(string fromTicker) => fromTicker switch
    {
        "xmr" => 5m,
        "btc" => 0.05m,
        "eth" => 1m,
        "usdt" => 1000m,
        _ => 1m,
    };

    public string From { get; private set; } = "btc";
    public string To { get; private set; } = "xmr";
    public string Asset { get; private set; } = "btc";
    public bool SellingXmr { get; private set; }
    public decimal Amount { get; private set; }
    public string AmountInput { get; private set; } = string.Empty;

    /// <summary>True when the user actually supplied a valid amount (vs. the default fallback).</summary>
    public bool AmountProvided { get; private set; }

    public PriceBoardView Pair { get; private set; } = PairCatalog.Btc;

    /// <summary>True once the user has actually asked for a quote (submitted the form).</summary>
    public bool Quoted { get; private set; }

    /// <summary>The no-JavaScript fallback: fetch every quote server-side and render the table
    /// (slower, one page load). With JS the page instead streams quotes in one by one.</summary>
    public bool NoJs { get; private set; }

    /// <summary>Fixed-rate mode (locked at quote time) vs the default floating rate.</summary>
    public bool Fixed { get; private set; }

    public IReadOnlyList<SwapQuoteRow> Results { get; private set; } = System.Array.Empty<SwapQuoteRow>();

    public int RespondedCount => Results.Count;

    public int ReceiveDecimals => SellingXmr ? Pair.Decimals : 4;
    public string FromUpper => Label(From);
    public string ToUpper => Label(To);
    public string AssetUpper => Label(Asset);

    /// <summary>Network tag for the non-XMR asset — only USDT is ambiguous (ERC-20 / Ethereum).</summary>
    public string AssetNetwork => Asset == "usdt" ? "ERC20" : string.Empty;

    // "USDT" carries its network so nobody sends the wrong-chain token; BTC/ETH are unambiguous.
    private static string Label(string t) => t switch
    {
        "xmr" => "XMR",
        "usdt" => "USDT",
        _ => t.ToUpperInvariant(),
    };


    public async Task OnGetAsync(
        string? from, string? to, string? asset, string? dir, string? amount, string? go, string? nojs,
        string? rate, CancellationToken ct)
    {
        Quoted = !string.IsNullOrWhiteSpace(go);
        NoJs = !string.IsNullOrWhiteSpace(nojs);
        Fixed = string.Equals((rate ?? string.Empty).Trim(), "fixed", System.StringComparison.OrdinalIgnoreCase);

        // ---- Resolve direction. Exactly one side is always XMR; default is BTC → XMR. ----
        var a = Norm(asset);
        if (a is "btc" or "eth" or "usdt")
        {
            Asset = a;
            // dir is a DIRECTION token ("sell" = XMR→asset, "buy" = asset→XMR), NOT a ticker —
            // compare it raw, don't run it through the ticker normalizer.
            SellingXmr = string.Equals((dir ?? string.Empty).Trim(), "sell", System.StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var f = Norm(from);
            var t = Norm(to);
            if (f == "xmr" && (t is "btc" or "eth" or "usdt")) { Asset = t; SellingXmr = true; }
            else if (t == "xmr" && (f is "btc" or "eth" or "usdt")) { Asset = f; SellingXmr = false; }
            else { Asset = "btc"; SellingXmr = false; } // default: Bitcoin → Monero
        }

        From = SellingXmr ? "xmr" : Asset;
        To = SellingXmr ? Asset : "xmr";
        Pair = Asset switch { "eth" => PairCatalog.Eth, "usdt" => PairCatalog.Usdt, _ => PairCatalog.Btc };

        // ---- Amount (denominated in the "from" side). Falls back to a sensible default for the
        //      calculation, but the textbox stays EMPTY by default; once a quote is shown we put
        //      the actual amount being quoted into the box so it's clear what the results are for.
        var rawAmount = (amount ?? string.Empty).Trim();
        AmountProvided = decimal.TryParse(rawAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt)
                         && amt > 0;
        if (!AmountProvided)
        {
            amt = DefaultAmountFor(From);
        }

        Amount = amt;
        AmountInput = Quoted
            ? amt.ToString("0.########", CultureInfo.InvariantCulture) // show the amount being quoted
            : rawAmount;                                               // fresh landing → empty box

        // With JS the page streams quotes in one by one from /swap/stream; we only fetch them
        // server-side for the no-JS fallback (?nojs=1), which renders the finished table.
        if (!Quoted || !NoJs)
        {
            return;
        }

        // ---- No-JS path: fan out to every exchange with the EXACT amount and render once complete.
        //      A rate for 0.00001 BTC is nothing like the rate for 10 BTC, and an amount below an
        //      exchange's minimum simply returns no quote and drops out. ----
        var best = new Dictionary<string, SwapQuoteRow>(System.StringComparer.OrdinalIgnoreCase);
        await foreach (var q in this.swap.GetQuotesStream(From, To, Amount, Fixed, ct))
        {
            var nk = new string(q.SiteName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (!best.TryGetValue(nk, out var existing) || q.Receive > existing.Receive)
            {
                best[nk] = q;
            }
        }

        var list = best.Values.ToList();

        // Drop nonsensical outliers (a mis-scaled quote): keep quotes within a band around the
        // median once there are enough to vote.
        if (list.Count >= 4)
        {
            var ordered = list.Select(x => x.Receive).OrderBy(x => x).ToList();
            var median = ordered[ordered.Count / 2];
            if (median > 0)
            {
                var lo = median * 0.5m;
                var hi = median * 2m;
                list = list.Where(x => x.Receive >= lo && x.Receive <= hi).ToList();
            }
        }

        // Best rate first — sponsors are highlighted in the view, not floated above better rates.
        Results = list.OrderByDescending(x => x.Receive).ToList();
    }

    private static string Norm(string? s)
    {
        s = (s ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "xmr" or "monero" => "xmr",
            "btc" or "bitcoin" => "btc",
            "eth" or "ethereum" => "eth",
            "usdt" or "tether" or "usdttrc" or "usdt-trc20" => "usdt",
            _ => string.Empty,
        };
    }
}
