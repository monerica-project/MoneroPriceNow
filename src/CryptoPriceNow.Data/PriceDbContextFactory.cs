using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CryptoPriceNow.Data;

/// <summary>
/// Used only by `dotnet ef migrations add ...` at design time.
/// No real database is contacted when adding a migration — the connection
/// string just needs to be syntactically valid. Override with the
/// PRICEDB_CONNECTION env var if you want `dotnet ef database update`
/// to hit a specific local database.
/// </summary>
public sealed class PriceDbContextFactory : IDesignTimeDbContextFactory<PriceDbContext>
{
    public PriceDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("PRICEDB_CONNECTION")
                 ?? "Host=localhost;Port=5432;Database=cryptopricenow;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<PriceDbContext>()
            .UseNpgsql(cs)
            .Options;

        return new PriceDbContext(options);
    }
}
