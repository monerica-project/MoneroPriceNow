using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace WebApplication1.Pages
{
    public class SponsorsModel : PageModel
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        // Tier display order, mirroring the homepage sponsor section.
        private static readonly string[] TierOrder =
            { "MainSponsor", "CategorySponsor", "SubCategorySponsor", "SubSponsor" };

        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;

        public SponsorsModel(IHttpClientFactory httpFactory, IConfiguration config)
        {
            _httpFactory = httpFactory;
            _config = config;
        }

        /// <summary>Sponsor groups in tier order. Empty if the feed is unavailable.</summary>
        public IReadOnlyList<SponsorGroup> Groups { get; private set; } = Array.Empty<SponsorGroup>();

        /// <summary>True when the feed could not be loaded (so the view can still show the CTA).</summary>
        public bool FeedUnavailable { get; private set; }

        public async Task OnGetAsync(CancellationToken ct)
        {
            var sourceUrl = _config["Sponsors:SourceUrl"];
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                FeedUnavailable = true;
                return;
            }

            try
            {
                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var json = await client.GetStringAsync(sourceUrl, ct);

                var all = JsonSerializer.Deserialize<List<Sponsor>>(json, JsonOpts) ?? new();

                var now = DateTimeOffset.UtcNow;
                var active = all
                    .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                    .Where(s => s.ExpirationDate is null || s.ExpirationDate > now)
                    .ToList();

                Groups = active
                    .GroupBy(s => s.SponsorshipType ?? "Other")
                    .OrderBy(g => TierRank(g.Key))
                    .Select(g => new SponsorGroup(
                        TierLabel(g.Key),
                        g.OrderByDescending(s => s.ExpirationDate ?? DateTimeOffset.MaxValue).ToList()))
                    .ToList();

                if (Groups.Count == 0) FeedUnavailable = false; // loaded, just empty
            }
            catch
            {
                FeedUnavailable = true;
            }
        }

        private static int TierRank(string key)
        {
            var idx = Array.IndexOf(TierOrder, key);
            return idx < 0 ? 99 : idx;
        }

        private static string TierLabel(string key) => key switch
        {
            "MainSponsor" => "Main Sponsors",
            "CategorySponsor" => "Category Sponsors",
            "SubCategorySponsor" => "Subcategory Sponsors",
            "SubSponsor" => "Sub Sponsors",
            _ => "Sponsors"
        };

        public sealed record SponsorGroup(string Label, IReadOnlyList<Sponsor> Sponsors);

        public sealed class Sponsor
        {
            public string? Name { get; set; }
            public string? Link { get; set; }
            public string? Description { get; set; }
            public string? SponsorshipType { get; set; }
            public DateTimeOffset? ExpirationDate { get; set; }
        }
    }
}
