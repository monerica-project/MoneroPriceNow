using System.Text.Json;
using CryptoPriceNow.Services;
using CryptoPriceNow.Web.Models;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CryptoPriceNow.Pages;

/// <summary>
/// A fully server-rendered version of the price board for visitors with JavaScript
/// disabled (e.g. Tor). No scripts — the page auto-refreshes via a meta refresh.
/// </summary>
public sealed class NoJsModel : PageModel
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IPriceService prices;
    private readonly IHttpClientFactory httpFactory;
    private readonly IConfiguration config;

    public NoJsModel(IPriceService prices, IHttpClientFactory httpFactory, IConfiguration config)
    {
        this.prices = prices;
        this.httpFactory = httpFactory;
        this.config = config;
    }

    public IReadOnlyList<TwoWayPriceRow> Rows { get; private set; } = Array.Empty<TwoWayPriceRow>();

    public decimal? MidUsdt { get; private set; }

    public IReadOnlyList<SponsorItem> MainSponsors { get; private set; } = Array.Empty<SponsorItem>();

    public DateTimeOffset AsOfUtc { get; private set; } = DateTimeOffset.UtcNow;

    // Active sponsors keyed by normalized name, for matching price-table exchanges.
    private Dictionary<string, SponsorItem> byName = new();

    private static string Norm(string? s) =>
        new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>The sponsor matching this exchange name, or null. Paid sponsors must NOT be
    /// linked through an affiliate URL, so callers use the sponsor's plain link instead.</summary>
    public SponsorItem? SponsorFor(string? exchangeName)
    {
        if (string.IsNullOrWhiteSpace(exchangeName))
        {
            return null;
        }

        return this.byName.TryGetValue(Norm(exchangeName), out var s) ? s : null;
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            this.Rows = await this.prices.GetTwoWayPricesAsync("XMR", "USDTTRC", ct);
            var mids = this.Rows
                .Where(r => r.Buy.HasValue && r.Sell.HasValue)
                .Select(r => (r.Buy!.Value + r.Sell!.Value) / 2m)
                .ToList();
            if (mids.Count > 0)
            {
                this.MidUsdt = Math.Round(mids.Average(), 2);
            }
        }
        catch
        {
            // leave Rows empty — the page still renders with the "unavailable" note.
        }

        var sourceUrl = this.config["Sponsors:SourceUrl"];
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            try
            {
                var client = this.httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var json = await client.GetStringAsync(sourceUrl, ct);
                var all = JsonSerializer.Deserialize<List<SponsorItem>>(json, JsonOpts) ?? new();
                var now = DateTimeOffset.UtcNow;
                var active = all
                    .Where(s => !string.IsNullOrWhiteSpace(s.Name) && (s.ExpirationDate is null || s.ExpirationDate > now))
                    .ToList();
                this.byName = active
                    .GroupBy(s => Norm(s.Name))
                    .ToDictionary(g => g.Key, g => g.First());
                this.MainSponsors = active.Where(s => s.SponsorshipType == "MainSponsor").ToList();
            }
            catch
            {
                // sponsors optional — ignore feed failure.
            }
        }
    }

    public sealed class SponsorItem
    {
        public string? Name { get; set; }
        public string? Link { get; set; }
        public string? Description { get; set; }
        public string? SponsorshipType { get; set; }
        public DateTimeOffset? ExpirationDate { get; set; }
    }
}
