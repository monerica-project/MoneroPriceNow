using CryptoPriceNow.Data.Interfaces;
using CryptoPriceNow.Data.Options;
using CryptoPriceNow.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CryptoPriceNow.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires up the Postgres quote store. If ConnectionStrings:PriceDb is
    /// missing or empty, registers a no-op sink instead — the site runs
    /// exactly as before with zero database dependency. This is what keeps
    /// local dev and any box without Postgres working unchanged.
    /// </summary>
    public static IServiceCollection AddCryptoPriceNowData(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<QuoteLoggingOptions>(config.GetSection("QuoteLogging"));

        var cs = config.GetConnectionString("PriceDb");
        if (string.IsNullOrWhiteSpace(cs))
        {
            services.AddSingleton<IPriceQuoteSink, NullPriceQuoteSink>();
            return services;
        }

        services.AddDbContextFactory<PriceDbContext>(o => o.UseNpgsql(cs));

        // One instance serves as both the sink (producer side) and the
        // hosted background consumer.
        services.AddSingleton<PriceQuoteLogger>();
        services.AddSingleton<IPriceQuoteSink>(sp => sp.GetRequiredService<PriceQuoteLogger>());
        services.AddHostedService(sp => sp.GetRequiredService<PriceQuoteLogger>());

        services.AddSingleton<PriceHistoryService>();

        return services;
    }
}
