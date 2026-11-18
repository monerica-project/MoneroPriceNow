using ExchangeServices.Abstractions;

namespace CryptoPriceNow.Web.Support;

/// <summary>
/// The exchange keys this site actually has a client for, read once at startup. Public
/// endpoints that take an exchange key (such as /api/history?exchange=) accept only these,
/// so visitors cannot make the server run a database query for arbitrary strings.
/// </summary>
public sealed class KnownExchangeKeys
{
    private readonly Dictionary<string, string> keys;

    public KnownExchangeKeys(IEnumerable<IExchangePriceApi> apis)
    {
        this.keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var api in apis)
        {
            if (!string.IsNullOrWhiteSpace(api.ExchangeKey))
            {
                this.keys.TryAdd(api.ExchangeKey.Trim(), api.ExchangeKey.Trim());
            }
        }
    }

    /// <summary>
    /// True when <paramref name="key"/> names a known exchange (any letter case); gives back the
    /// key exactly as the exchange's client spells it, which is how it is stored in the database.
    /// </summary>
    public bool TryGetCanonical(string? key, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(key) || !this.keys.TryGetValue(key.Trim(), out var found))
        {
            return false;
        }

        canonical = found;
        return true;
    }
}
