using System.Text;

namespace CryptoPriceNow.Data.Services;

/// <summary>
/// Slug used for /exchange/{slug} URLs. Deliberately identical to the client-side
/// monericaSlug() in moneropricenow.js (lower-case, every non-alphanumeric run
/// collapsed to a single hyphen, trimmed) so a homepage row's link and the page
/// that answers it always agree — derived from the exchange's display name.
/// </summary>
public static class ExchangeSlug
{
    public static string From(string? siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(siteName.Length);
        var pendingHyphen = false;

        foreach (var ch in siteName.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                if (pendingHyphen && sb.Length > 0)
                {
                    sb.Append('-');
                }

                pendingHyphen = false;
                sb.Append(ch);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return sb.ToString();
    }
}
