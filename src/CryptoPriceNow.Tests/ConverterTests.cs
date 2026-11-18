using CryptoPriceNow.Pages;
using CryptoPriceNow.Web.Support;
using Xunit;

namespace CryptoPriceNow.Tests;

/// <summary>Currency converter pages (/convert/xmr-to-eur) and the price history page.</summary>
public class ConverterTests
{
    private const string Sample = """
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
          <Cube><Cube time="2026-11-18">
            <Cube currency="USD" rate="1.2500"/><Cube currency="GBP" rate="0.8750"/><Cube currency="JPY" rate="187.50"/>
          </Cube></Cube>
        </gesmes:Envelope>
        """;

    [Fact]
    public void BankRates_AreRebasedOnTheUsDollar()
    {
        var rates = FxRateService.Parse(Sample);
        Assert.NotNull(rates);
        Assert.Equal(new DateOnly(2026, 11, 18), rates!.Date);
        Assert.Equal(1m, rates.UsdTo["USD"]);
        Assert.Equal(0.8m, rates.UsdTo["EUR"]);   // 1 / 1.25
        Assert.Equal(0.7m, rates.UsdTo["GBP"]);   // 0.875 / 1.25
        Assert.Equal(150m, rates.UsdTo["JPY"]);   // 187.5 / 1.25
    }

    [Fact]
    public void RatesWithoutADollarRate_AreRejected()
    {
        Assert.Null(FxRateService.Parse("<Envelope><Cube><Cube time=\"2026-11-18\"><Cube currency=\"GBP\" rate=\"0.87\"/></Cube></Cube></Envelope>"));
    }

    [Fact]
    public void EveryListedCurrency_CanBeFoundByItsCode_AndUnknownOnesCannot()
    {
        Assert.All(FiatCatalog.All, f => Assert.Same(f, FiatCatalog.Find(f.Slug)));
        Assert.Null(FiatCatalog.Find("xyz"));
        Assert.Null(FiatCatalog.Find(null));
    }

    [Fact]
    public void History_KeepsTheLastPriceOfEachDay_NewestFirst()
    {
        long At(int day, int hour) => new DateTimeOffset(2026, 11, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var series = new List<MillionaireService.SeriesPoint>
        {
            new(At(16, 2), 2500m), new(At(16, 23), 2000m),   // last of the day: 1,000,000 / 2000 = 500
            new(At(17, 12), 4000m),                           // 250
        };
        var days = HistoryModel.ToDays(series);
        Assert.Equal(2, days.Count);
        Assert.Equal(new DateOnly(2026, 11, 17), days[0].Date);
        Assert.Equal(250m, days[0].Price);
        Assert.Equal(500m, days[1].Price);
    }
}
