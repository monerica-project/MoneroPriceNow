using System.Globalization;
using System.Text.Json;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
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
    private readonly IPriceService prices;
    private readonly IHttpClientFactory httpFactory;
    private readonly IConfiguration config;

    public SwapModel(IPriceService prices, IHttpClientFactory httpFactory, IConfiguration config)
    {
        this.prices = prices;
        this.httpFactory = httpFactory;
        this.config = config;
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

    public sealed record SwapQuoteRow(
        string SiteName,
        decimal Receive,
        decimal PerXmr,       // asset units per 1 XMR
        char? PrivacyLevel,
        string RateType,
        bool IsSponsor,
        string? OutHref,      // sponsor → direct link; else affiliate URL
        string MonericaUrl);  // exchange name → its Monerica listing

    public async Task OnGetAsync(
        string? from, string? to, string? asset, string? dir, string? amount, string? go, CancellationToken ct)
    {
        Quoted = !string.IsNullOrWhiteSpace(go);

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

        if (!Quoted)
        {
            return; // form-only view — no exchange list until a quote is requested
        }

        // ---- Quotes. Sell direction uses one-way SELL rows (includes sell-only venues like
        //      xmr2cex); buy direction uses the two-way board rows (Buy side). ----
        var src = SellingXmr
            ? await this.prices.GetSellRowsAsync(Pair.Base, Pair.ApiQuote, ct)
            : await this.prices.GetTwoWayPricesAsync(Pair.Base, Pair.ApiQuote, ct);

        var sponsors = await GetSponsorsAsync(ct);

        // One row per exchange (deduped by normalized name), best rate kept.
        var best = new Dictionary<string, SwapQuoteRow>(System.StringComparer.Ordinal);
        foreach (var r in src)
        {
            var perXmr = SellingXmr ? r.Sell : r.Buy;
            if (perXmr is not decimal per || per <= 0)
            {
                continue;
            }

            var receive = SellingXmr ? Amount * per : Amount / per;
            if (receive <= 0)
            {
                continue;
            }

            var nk = NormName(r.SiteName);
            var isSponsor = sponsors.TryGetValue(nk, out var sponsorLink) && !string.IsNullOrWhiteSpace(sponsorLink);
            var outHref = isSponsor ? sponsorLink : r.SiteUrl; // sponsors link direct, not via affiliate

            var row = new SwapQuoteRow(
                r.SiteName, receive, per, r.PrivacyLevel, r.RateType,
                isSponsor, outHref, "https://monerica.com/site/" + Slugify(r.SiteName));

            if (!best.TryGetValue(nk, out var existing) || row.Receive > existing.Receive)
            {
                best[nk] = row;
            }
        }

        var list = best.Values.ToList();

        // Drop nonsensical outliers (a mis-scaled one-way sell quote): keep quotes within a band
        // around the median once there are enough to vote.
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

    // ── Sponsor lookup (normalized name → direct link), briefly cached across requests. ──
    private static readonly SemaphoreSlim SponsorLock = new(1, 1);
    private static Dictionary<string, string> sponsorCache = new(System.StringComparer.Ordinal);
    private static DateTime sponsorCachedAt = DateTime.MinValue;

    private async Task<Dictionary<string, string>> GetSponsorsAsync(CancellationToken ct)
    {
        var ttl = TimeSpan.FromMinutes(this.config.GetValue("Sponsors:CacheTtlMinutes", 20));
        if (sponsorCachedAt != DateTime.MinValue && DateTime.UtcNow - sponsorCachedAt < ttl)
        {
            return sponsorCache;
        }

        await SponsorLock.WaitAsync(ct);
        try
        {
            if (sponsorCachedAt != DateTime.MinValue && DateTime.UtcNow - sponsorCachedAt < ttl)
            {
                return sponsorCache;
            }

            var map = new Dictionary<string, string>(System.StringComparer.Ordinal);
            var url = this.config["Sponsors:SourceUrl"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                var client = this.httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var json = await client.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var link = el.TryGetProperty("link", out var l) ? l.GetString() : null;

                        var active = true;
                        if (el.TryGetProperty("expirationDate", out var e) && e.ValueKind == JsonValueKind.String
                            && DateTime.TryParse(e.GetString(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal, out var exp))
                        {
                            active = exp > DateTime.UtcNow;
                        }

                        if (active && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(link))
                        {
                            map[NormName(name)] = link!;
                        }
                    }
                }
            }

            sponsorCache = map;
            sponsorCachedAt = DateTime.UtcNow;
            return map;
        }
        catch
        {
            // On failure keep whatever we had (possibly empty) and don't hammer the source.
            sponsorCachedAt = DateTime.UtcNow;
            return sponsorCache;
        }
        finally
        {
            SponsorLock.Release();
        }
    }

    // Matches the board's normName: lower-case, strip everything but a-z0-9 (sponsor key match).
    private static string NormName(string? s)
        => new string((s ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // Matches the board's exchangeSlug: lower-case, non-alphanumeric runs → "-", trim "-".
    private static string Slugify(string? s)
    {
        var lower = (s ?? string.Empty).ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastDash = false;
        foreach (var c in lower)
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); lastDash = false; }
            else if (!lastDash) { sb.Append('-'); lastDash = true; }
        }

        return sb.ToString().Trim('-');
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
