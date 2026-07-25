using CryptoPriceNow.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPriceNow.Data.Services;

/// <summary>Registry lookups for the per-exchange pages (/exchange/{slug}).</summary>
public sealed class ExchangeDirectoryService
{
    private readonly IDbContextFactory<PriceDbContext> _dbFactory;

    public ExchangeDirectoryService(IDbContextFactory<PriceDbContext> dbFactory)
        => _dbFactory = dbFactory;

    /// <summary>All active exchanges, name-sorted. Used for the sitemap and any index.</summary>
    public async Task<IReadOnlyList<Exchange>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Exchanges
            .Where(e => e.IsActive)
            .OrderBy(e => e.SiteName)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolve an active exchange from a /exchange/{slug} URL by matching the slug of its
    /// SiteName. Loaded into memory and matched in code (the slug isn't a stored column),
    /// which is fine at this table's size (tens of rows). Returns null if none match.
    /// </summary>
    public async Task<Exchange?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        slug = slug.Trim().ToLowerInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var active = await db.Exchanges.Where(e => e.IsActive).ToListAsync(ct);

        return active.FirstOrDefault(e =>
            string.Equals(ExchangeSlug.From(e.SiteName), slug, StringComparison.Ordinal));
    }

    /// <summary>
    /// The distinct normalized pairs this exchange has ever produced a quote for
    /// (e.g. "XMR/USDT:Tron", "XMR/BTC"). Lets the page show only the pairs the
    /// exchange actually supports.
    /// </summary>
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
