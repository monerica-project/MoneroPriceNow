using CryptoPriceNow.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CryptoPriceNow.Data;

public sealed class PriceDbContext : DbContext
{
    public PriceDbContext(DbContextOptions<PriceDbContext> options) : base(options) { }

    public DbSet<Exchange> Exchanges => Set<Exchange>();
    public DbSet<PriceQuote> PriceQuotes => Set<PriceQuote>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Exchange>(e =>
        {
            e.Property(x => x.ExchangeKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.SiteName).HasMaxLength(128).IsRequired();
            e.Property(x => x.SiteUrl).HasMaxLength(512);
            e.Property(x => x.PrivacyLevel).HasMaxLength(1);
            e.Property(x => x.RateType).HasMaxLength(16).IsRequired();
            e.HasIndex(x => x.ExchangeKey).IsUnique();
        });

        b.Entity<PriceQuote>(e =>
        {
            e.Property(x => x.Pair).HasMaxLength(64).IsRequired();
            e.Property(x => x.RateType).HasMaxLength(16).IsRequired();
            e.Property(x => x.Buy).HasPrecision(28, 10);
            e.Property(x => x.Sell).HasPrecision(28, 10);

            e.HasOne(x => x.Exchange)
             .WithMany(x => x.Quotes)
             .HasForeignKey(x => x.ExchangeId)
             .OnDelete(DeleteBehavior.Cascade);

            // Charting query: WHERE Pair = ? AND TimestampUtc >= ? GROUP BY date_bin(...)
            e.HasIndex(x => new { x.Pair, x.TimestampUtc });

            // Per-exchange history / debugging
            e.HasIndex(x => new { x.ExchangeId, x.TimestampUtc });

            // Retention pruning: DELETE WHERE TimestampUtc < cutoff
            e.HasIndex(x => x.TimestampUtc);
        });
    }
}
