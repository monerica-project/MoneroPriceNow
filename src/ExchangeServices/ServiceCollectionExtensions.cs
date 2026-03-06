using ExchangeServices.Abstractions;
using ExchangeServices.Implementations;
using ExchangeServices.Implmentations.PegasusSwap;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using ExchangeServices.SageSwap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Buffers.Text;
using System.Net.Http.Headers;

namespace ExchangeServices;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddExchangeServices(this IServiceCollection services, IConfiguration config)
    {

 

        // checked
        services.AddSageSwap(config);
        services.AddStealthEx(config);
        services.AddChangeNow(config);
        services.AddBaltex(config);
        services.AddPegasusSwap(config);
        services.AddExolix(config);
        services.AddFixedFloat(config);
        services.AddEtzSwap(config);
        services.AddLetsExchange(config);
        services.AddFuguSwap(config);
        services.AddCCECash(config);
        services.AddDevilExchange(config);
        services.AddSimpleSwap(config);
        
        
        //  services.AddQuickEx(config); // auth issues

        //      services.AddChangee(config); // auth issues 
        //  services.AddSwapuz(config);

        //services.AddXgram(config);
        // services.AddNanswap(config);


        // 

        //  services.AddOctoSwap(config);
        //
        //   
        //   
        //   
        //   
        //
        //   services.AddWagyu(config);
        // services.AddWizardSwap(config);
        //  
        //    
        // services.AddCypherGoat(config);
        // services.AddXChange(config);
        //
        return services;
    }

    public static IServiceCollection AddSimpleSwap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SimpleSwapOptions>(config.GetSection("SimpleSwap"));

        services.AddHttpClient<ISimpleSwapClient, SimpleSwapClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<SimpleSwapOptions>>().Value;
            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<ISimpleSwapClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<ISimpleSwapClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<ISimpleSwapClient>());

        return services;
    }


    public static IServiceCollection AddQuickEx(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<QuickExOptions>(config.GetSection("QuickEx"));

        services.AddHttpClient<IQuickExClient, QuickExClient>(client =>
        {
            var baseUrl = config["QuickEx:BaseUrl"] ?? "https://quickex.io";
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Accept
                  .Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        });

        // ✅ ADD THESE THREE LINES — without them PriceService never sees QuickEx
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IQuickExClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IQuickExClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IQuickExClient>());

        return services;
    }

    public static IServiceCollection AddChangee(this IServiceCollection services, IConfiguration config)
    {
        // ── Changee ──────────────────────────────────────────────────────────────────
        // Add to your Program.cs / service registration

        services.Configure<ChangeeOptions>(
        config.GetSection("Changee"));

        services.AddHttpClient<IChangeeClient, ChangeeClient>(client =>
        {
            client.BaseAddress = new Uri(
               config["Changee:BaseUrl"] ?? "https://changee.com");
        });

        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IChangeeClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IChangeeClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IChangeeClient>());

        return services;
    }

    public static IServiceCollection AddSwapuz(this IServiceCollection services, IConfiguration config)
    {

        // ── Swapuz ──────────────────────────────────────────────────────────────────
        // Add to your Program.cs / service registration

        services.Configure<SwapuzOptions>(
        config.GetSection("Swapuz"));

        services.AddHttpClient<ISwapuzClient, SwapuzClient>(client =>
        {
            client.BaseAddress = new Uri(
                config["Swapuz:BaseUrl"] ?? "https://api.swapuz.com");
        });

        // Register as both price and currency API (DI will resolve all IExchangePriceApi etc.)
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<ISwapuzClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<ISwapuzClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<ISwapuzClient>());

        return services;
    }

    public static IServiceCollection AddWizardSwap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WizardSwapOptions>(config.GetSection("WizardSwap"));

        services.AddHttpClient<IWizardSwapClient, WizardSwapClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<WizardSwapOptions>>().Value;

            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // IMPORTANT: client enforces timeouts

            if (client.DefaultRequestHeaders.Accept.Count == 0)
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IWizardSwapClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IWizardSwapClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IWizardSwapClient>());

        return services;
    }

    public static IServiceCollection AddChangeNow(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ChangeNowOptions>(config.GetSection("ChangeNow"));

        services.AddHttpClient<IChangeNowClient, ChangeNowClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<ChangeNowOptions>>().Value;

            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // IMPORTANT: our client enforces per-request timeouts

            if (client.DefaultRequestHeaders.Accept.Count == 0)
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // Make it discoverable by your hub enumerations
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IChangeNowClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IChangeNowClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IChangeNowClient>());

        return services;
    }


    public static IServiceCollection AddExolix(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ExolixOptions>(config.GetSection("Exolix"));

        services.AddHttpClient<IExolixClient, ExolixClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<ExolixOptions>>().Value;

            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // ✅ new way: per-request timeouts

            if (client.DefaultRequestHeaders.Accept.Count == 0)
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Exolix: Authorization: {API Key} (no scheme)
            if (!string.IsNullOrWhiteSpace(opt.ApiKey))
                client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", opt.ApiKey);

            if (client.DefaultRequestHeaders.UserAgent.Count == 0 && !string.IsNullOrWhiteSpace(opt.UserAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
        });

        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IExolixClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IExolixClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IExolixClient>());

        return services;
    }
    public static IServiceCollection AddLetsExchange(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LetsExchangeOptions>(config.GetSection("LetsExchange"));

        services.AddHttpClient<ILetsExchangeClient, LetsExchangeClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<LetsExchangeOptions>>().Value;

            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // IMPORTANT: SafeHttp handles timeout

            // optional default Accept
            if (client.DefaultRequestHeaders.Accept.Count == 0)
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<ILetsExchangeClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<ILetsExchangeClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<ILetsExchangeClient>());

        return services;
    }

    public static IServiceCollection AddCypherGoat(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<CypherGoatOptions>(cfg.GetSection("CypherGoat"));

        services.AddHttpClient<ICypherGoatClient, CypherGoatClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<CypherGoatOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/'));
                client.Timeout = Timeout.InfiniteTimeSpan; // SafeHttp controls timeout
            });

        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<ICypherGoatClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<ICypherGoatClient>());

        return services;
    }

    public static IServiceCollection AddXgram(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<XgramOptions>(cfg.GetSection("Xgram"));

        services.AddHttpClient<IXgramClient, XgramClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<XgramOptions>>().Value;

            // we'll fix BaseAddress below too
            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // SafeHttp controls timeout
        });

        // ✅ THIS is what makes it show up in PriceService enumerations
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IXgramClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IXgramClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IXgramClient>());

        return services;
    }

    public static IServiceCollection AddCCECash(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<CCECashOptions>(cfg.GetSection("CCECash"));

        services.AddHttpClient<ICCECashClient, CCECashClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<CCECashOptions>>().Value;

            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // ✅ if you're doing your own SafeHttp timeouts

            if (client.DefaultRequestHeaders.Accept.Count == 0)
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
 
        });

        // ✅ THIS is what makes it appear in PriceService enumerations
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<ICCECashClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<ICCECashClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<ICCECashClient>());

        return services;
    }

    public static IServiceCollection AddSageSwap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SageSwapOptions>(config.GetSection("SageSwap"));

        services.AddHttpClient<ISageSwapClient, SageSwapClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<SageSwapOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl); // e.g. https://sageswap.io/api
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 60));

                if (!string.IsNullOrWhiteSpace(opt.Token))
                    client.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", opt.Token);

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                }
            });
        services.Configure<SageSwapOptions>(config.GetSection("SageSwap"));

        // expose through generic abstractions too (so web can use IEnumerable<>)
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<ISageSwapClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<ISageSwapClient>());
        return services;
    }
    public static IServiceCollection AddFixedFloat(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FixedFloatOptions>(config.GetSection("FixedFloat"));

        services.AddHttpClient<IFixedFloatClient, FixedFloatClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<FixedFloatOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/'));
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 120));
            });

        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IFixedFloatClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IFixedFloatClient>());

        return services;
    }

    public static IServiceCollection AddFuguSwap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FuguSwapOptions>(config.GetSection("FuguSwap"));

        services.AddHttpClient<IFuguSwapClient, FuguSwapClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<FuguSwapOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/'));
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 120));
            });

        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IFuguSwapClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IFuguSwapClient>());

        return services;
    }
    public static IServiceCollection AddEtzSwap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EtzSwapOptions>(config.GetSection("EtzSwap"));

        services.AddHttpClient<IEtzSwapClient, EtzSwapClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<EtzSwapOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/'));
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 60));

                if (client.DefaultRequestHeaders.Accept.Count == 0)
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // Expose through the generic abstractions so PriceService can enumerate them
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IEtzSwapClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IEtzSwapClient>());

        return services;
    }

    public static IServiceCollection AddNanswap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<NanswapOptions>(config.GetSection("Nanswap"));

        services.AddHttpClient<INanswapClient, NanswapClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<NanswapOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 60));

                if (client.DefaultRequestHeaders.Accept.Count == 0)
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                }
            });

        // Expose generics for your hub (IEnumerable<>)
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<INanswapClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<INanswapClient>());

        return services;
    }

    public static IServiceCollection AddDevilExchange(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DevilExchangeOptions>(config.GetSection("DevilExchange"));

        services.AddHttpClient<IDevilExchangeClient, DevilExchangeClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<DevilExchangeOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl); // https://devil.exchange
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 3, 60));

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                }
            });

        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IDevilExchangeClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IDevilExchangeClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IDevilExchangeClient>());

        return services;
    }

    public static IServiceCollection AddXChange(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<XChangeOptions>(config.GetSection("XChange"));

        services.AddHttpClient<IXChangeClient, XChangeClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<XChangeOptions>>().Value;
                client.BaseAddress = new Uri(opt.BaseUrl); // https://xchange.me/api/v1
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 3, 60));

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                }
            });

        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IXChangeClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IXChangeClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IXChangeClient>());

        return services;
    }

    public static IServiceCollection AddOctoSwap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OctoSwapOptions>(config.GetSection("OctoSwap"));

        services.AddHttpClient<IOctoSwapClient, OctoSwapClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<OctoSwapOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl); // include "/api" if you want
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 3, 60));

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                }
            });

        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IOctoSwapClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IOctoSwapClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IOctoSwapClient>());

        return services;
    }

    public static IServiceCollection AddPegasusSwap(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PegasusSwapOptions>(config.GetSection("PegasusSwap"));

        services.AddHttpClient<IPegasusSwapClient, PegasusSwapClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<PegasusSwapOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl); // https://api.pegasusswap.com
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 60));

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                }
            });

        // expose through generic abstractions (this is what your web PriceService enumerates)
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IPegasusSwapClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IPegasusSwapClient>());

        return services;
    }

    public static IServiceCollection AddWagyu(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<WagyuOptions>(config.GetSection("Wagyu"));

        services.AddHttpClient<IWagyuClient, WagyuClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<WagyuOptions>>().Value;

            client.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan; // IMPORTANT: WagyuClient handles per-attempt timeouts
        });

        // ✅ THIS is what makes it show up in your PriceService enumerations
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IWagyuClient>());
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IWagyuClient>());
        services.AddTransient<IExchangeBuyPriceApi>(sp => sp.GetRequiredService<IWagyuClient>());

        return services;
    }
    public static IServiceCollection AddStealthEx(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<StealthExOptions>(config.GetSection("StealthEx"));

        services.AddHttpClient<IStealthExClient, StealthExClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<StealthExOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl); // https://api.stealthex.io
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 60));

                // Default headers
                if (client.DefaultRequestHeaders.UserAgent.Count == 0 && !string.IsNullOrWhiteSpace(opt.UserAgent))
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);

                // Default auth header (client also sets per-request; either is fine)
                if (!string.IsNullOrWhiteSpace(opt.ApiKey))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiKey);
            });

        // ✅ expose to the hub service enumeration
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IStealthExClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IStealthExClient>());

        return services;
    }
    public static IServiceCollection AddBaltex(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BaltexOptions>(config.GetSection("Baltex"));

        services.AddHttpClient<IBaltexClient, BaltexClient>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opt = sp.GetRequiredService<IOptions<BaltexOptions>>().Value;

                client.BaseAddress = new Uri(opt.BaseUrl); // https://api.baltex.io
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 1, 60));

                if (client.DefaultRequestHeaders.UserAgent.Count == 0 &&
                    !string.IsNullOrWhiteSpace(opt.UserAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
                }

                // x-api-key is set per-request in the client (safe), but you can also set default:
                // if (!string.IsNullOrWhiteSpace(opt.ApiKey))
                //     client.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", opt.ApiKey);
            });

        // expose to the hub enumeration
        services.AddTransient<IExchangePriceApi>(sp => sp.GetRequiredService<IBaltexClient>());
        services.AddTransient<IExchangeCurrencyApi>(sp => sp.GetRequiredService<IBaltexClient>());

        return services;
    }
}