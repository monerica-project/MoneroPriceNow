using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;

namespace CryptoPriceNow.Web.Support;

/// <summary>A currency the converter pages support (/convert/xmr-to-eur …).</summary>
public sealed record Fiat(string Code, string Name, string Plural, string Symbol, int Decimals)
{
    public string Slug => Code.ToLowerInvariant();
}

public static class FiatCatalog
{
    public static readonly IReadOnlyList<Fiat> All =
    [
        new("USD", "US Dollar", "US dollars", "$", 2),
        new("EUR", "Euro", "euros", "€", 2),
        new("GBP", "British Pound", "British pounds", "£", 2),
        new("CAD", "Canadian Dollar", "Canadian dollars", "C$", 2),
        new("AUD", "Australian Dollar", "Australian dollars", "A$", 2),
        new("CHF", "Swiss Franc", "Swiss francs", "CHF ", 2),
        new("JPY", "Japanese Yen", "Japanese yen", "¥", 0),
        new("INR", "Indian Rupee", "Indian rupees", "₹", 2),
        new("BRL", "Brazilian Real", "Brazilian reais", "R$", 2),
        new("MXN", "Mexican Peso", "Mexican pesos", "MX$", 2),
    ];

    public static Fiat? Find(string? code) =>
        All.FirstOrDefault(f => f.Code.Equals(code?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Daily reference exchange rates from the European Central Bank (one small public file, fetched at
/// most twice a day and cached), turned into "1 US dollar = N units" for each supported currency.
/// The Monero price itself always comes from the exchanges; this only converts dollars onward.
/// </summary>
public sealed class FxRateService
{
    public const string SourceUrl = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    private const string CacheKey = "fx:usd-rates";

    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;

    public FxRateService(IHttpClientFactory http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public sealed record Rates(DateOnly Date, IReadOnlyDictionary<string, decimal> UsdTo);

    /// <summary>Null when the rates could not be fetched; callers then show dollars only.</summary>
    public async Task<Rates?> GetAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CacheKey, out Rates? cached))
        {
            return cached;
        }

        Rates? rates = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var xml = await _http.CreateClient().GetStringAsync(SourceUrl, timeout.Token);
            rates = Parse(xml);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Source unreachable: remember the miss briefly so a page view never waits on it twice.
        }

        _cache.Set(CacheKey, rates, rates is null ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(12));
        return rates;
    }

    /// <summary>Reads the bank's "1 euro = N units" file and re-bases it on the US dollar.</summary>
    public static Rates? Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var day = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Cube" && e.Attribute("time") is not null);
        if (day is null || !DateOnly.TryParse(day.Attribute("time")!.Value, CultureInfo.InvariantCulture, out var date))
        {
            return null;
        }

        var perEuro = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["EUR"] = 1m };
        foreach (var cube in day.Elements().Where(e => e.Name.LocalName == "Cube"))
        {
            var code = cube.Attribute("currency")?.Value;
            if (code is not null
                && decimal.TryParse(cube.Attribute("rate")?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
                && rate > 0m)
            {
                perEuro[code] = rate;
            }
        }

        if (!perEuro.TryGetValue("USD", out var usdPerEuro) || usdPerEuro <= 0m)
        {
            return null;
        }

        var usdTo = perEuro.ToDictionary(kv => kv.Key, kv => kv.Value / usdPerEuro, StringComparer.OrdinalIgnoreCase);
        usdTo["USD"] = 1m;
        return new Rates(date, usdTo);
    }
}
