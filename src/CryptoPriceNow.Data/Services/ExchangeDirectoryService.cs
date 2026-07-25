using CryptoPriceNow.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CryptoPriceNow.Data.Services;

/// <summary>Registry lookups for the per-exchange pages (/exchange/{slug}) and index.</summary>
public sealed class ExchangeDirectoryService
{
    private readonly IDbContextFactory<PriceDbContext> _dbFactory;
    private readonly int _activeWithinMinutes;

    public ExchangeDirectoryService(IDbContextFactory<PriceDbContext> dbFactory, IConfiguration config)
    {
        _dbFactory = dbFactory;
        // "Currently active" = quoted within this window. IsActive alone isn't enough — it's
        // set true on first sight and never cleared, so a delisted exchange keeps IsActive=true
        // but stops updating LastSeenUtc. Matching recency is what keeps the index/pages in sync
        // with what the live homepage actually shows.
        _activeWithinMinutes = config.GetValue<int>("Exchanges:ActiveWithinMinutes", 120);
    }

    private DateTimeOffset ActiveCutoff => DateTimeOffset.UtcNow.AddMinutes(-_activeWithinMinutes);

    /// <summary>
    /// Exchanges that are currently live — flagged active AND seen quoting within the recency
    /// window. Delisted/dormant exchanges (stale LastSeenUtc) are excluded. Name-sorted.
    /// Used for the index page and the sitemap.
    /// </summary>
    public async Task<IReadOnlyList<Exchange>> GetActiveAsync(CancellationToken ct = default)
    {
        var cutoff = ActiveCutoff;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Exchanges
            .Where(e => e.IsActive && e.LastSeenUtc >= cutoff)
            .OrderBy(e => e.SiteName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolve a currently-live exchange from a /exchange/{slug} URL by matching the slug of
    /// its SiteName. Delisted/dormant exchanges return null (so their page 404s and drops out
    /// alongside the index), keeping stale pages from lingering in search results.
    /// </summary>
    public async Task<Exchange?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        slug = slug.Trim().ToLowerInvariant();
        var cutoff = ActiveCutoff;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var live = await db.Exchanges
            .Where(e => e.IsActive && e.LastSeenUtc >= cutoff)
            .ToListAsync(ct);

        return live.FirstOrDefault(e =>
            string.Equals(ExchangeSlug.From(e.SiteName), slug, StringComparison.Ordinal));
    }

    /// <summary>The distinct normalized pairs this exchange has quoted (for the page's pair list).</summary>
    public async Task<IReadOnlyList<string>> GetQuotedPairsAsync(int exchangeId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.PriceQuotes
            .Where(q => q.ExchangeId == exchangeId)
            .Select(q => q.Pair)
            .Distinct()
            .ToListAsync(ct);
    }
}
