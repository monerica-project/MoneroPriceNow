using System.Globalization;
using System.Text;
using QRCoder;

namespace CryptoPriceNow.Web.Services;

/// <summary>
/// Builds a Monero payment invoice as a self-contained SVG image: a scannable QR code of the
/// standard <c>monero:</c> payment URI, plus the XMR amount and a rough USD estimate.
/// </summary>
public static class InvoiceBuilder
{
    private const string Base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        address = address.Trim();
        // Standard/subaddress = 95 chars; integrated = 106. Mainnet starts with 4 or 8.
        if (address.Length != 95 && address.Length != 106) return false;
        if (address[0] != '4' && address[0] != '8') return false;
        foreach (var c in address)
        {
            if (Base58.IndexOf(c) < 0) return false;
        }
        return true;
    }

    public static string BuildUri(string address, decimal xmr)
    {
        var amt = xmr.ToString("0.############", CultureInfo.InvariantCulture);
        return $"monero:{address}?tx_amount={amt}";
    }

    public static string BuildSvg(string address, decimal xmr, decimal? usd, string generatedUtc)
    {
        var uri = BuildUri(address, xmr);

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var pngBytes = new PngByteQRCode(data).GetGraphic(10);
        var qr = Convert.ToBase64String(pngBytes);

        var xmrStr = xmr.ToString("0.############", CultureInfo.InvariantCulture);
        var usdStr = usd is > 0 ? "≈ $" + usd.Value.ToString("N2", CultureInfo.InvariantCulture) + " USD" : "";

        // address wrapped into fixed-width lines
        var addrLines = new StringBuilder();
        int y = 566;
        for (int i = 0; i < address.Length; i += 32)
        {
            var seg = address.Substring(i, Math.Min(32, address.Length - i));
            addrLines.Append($"<text x=\"220\" y=\"{y}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"14\" fill=\"#333\">{X(seg)}</text>");
            y += 20;
        }

        return $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""440"" height=""660"" viewBox=""0 0 440 660"">
<rect width=""440"" height=""660"" fill=""#ffffff""/>
<rect x=""0"" y=""0"" width=""440"" height=""78"" fill=""#ff6600""/>
<g stroke=""#ffffff"" stroke-width=""6"" fill=""none"" stroke-linecap=""round"" stroke-linejoin=""round""><path d=""M34 54 L34 24 L52 48 L70 24 L70 54""/></g>
<text x=""92"" y=""38"" font-family=""Helvetica,Arial,sans-serif"" font-size=""24"" font-weight=""800"" fill=""#ffffff"">Monero Payment</text>
<text x=""92"" y=""62"" font-family=""Helvetica,Arial,sans-serif"" font-size=""14"" fill=""#ffe3d1"">Scan with any Monero wallet to pay</text>
<image href=""data:image/png;base64,{qr}"" x=""100"" y=""104"" width=""240"" height=""240""/>
<rect x=""98"" y=""102"" width=""244"" height=""244"" fill=""none"" stroke=""#eee"" stroke-width=""2""/>
<text x=""220"" y=""402"" text-anchor=""middle"" font-family=""Helvetica,Arial,sans-serif"" font-size=""40"" font-weight=""800"" fill=""#111"">{X(xmrStr)} XMR</text>
<text x=""220"" y=""436"" text-anchor=""middle"" font-family=""Helvetica,Arial,sans-serif"" font-size=""20"" fill=""#ff6600"" font-weight=""700"">{X(usdStr)}</text>
<line x1=""40"" y1=""470"" x2=""400"" y2=""470"" stroke=""#eee"" stroke-width=""2""/>
<text x=""220"" y=""506"" text-anchor=""middle"" font-family=""Helvetica,Arial,sans-serif"" font-size=""13"" fill=""#888"" letter-spacing=""1"">PAY TO THIS ADDRESS</text>
{addrLines}
<text x=""220"" y=""640"" text-anchor=""middle"" font-family=""Helvetica,Arial,sans-serif"" font-size=""13"" fill=""#aaa"">moneropricenow.com · rate as of {X(generatedUtc)} UTC</text>
</svg>";
    }

    private static string X(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
