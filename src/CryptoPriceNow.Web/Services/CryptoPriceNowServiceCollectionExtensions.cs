using CryptoPriceNow.Web.Models;
using ExchangeServices;
using ExchangeServices.Abstractions;
using ExchangeServices.SageSwap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;

namespace CryptoPriceNow.Services;

public static class CryptoPriceNowServiceCollectionExtensions
{
    public static IServiceCollection AddCryptoPriceNowServices(
        this IServiceCollection services, IConfiguration config)
    {
        services.AddExchangeServices(config);

        services.Configure<PriceServiceOptions>(config.GetSection("PriceService"));

        // ✅ Singleton — one instance for the lifetime of the app.
        // This means the IMemoryCache inside it is shared across ALL requests.
        // Scoped = new instance per request = cache is always empty = always slow.
        services.AddSingleton<IPriceService, PriceService>();

        services.AddHttpClient<ISageSwapClient, SageSwapClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<SageSwapOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 60));

                if (!string.IsNullOrWhiteSpace(opt.Token))
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", opt.Token);

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
            });

        services.AddTransient<IExchangeCurrencyApi>(
            sp => sp.GetRequiredService<ISageSwapClient>());

        return services;
    }
}