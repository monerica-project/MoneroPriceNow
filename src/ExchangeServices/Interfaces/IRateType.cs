namespace ExchangeServices.Interfaces
{
    /// <summary>
    /// Optional capability: declares whether the exchange's quoted rates are
    /// fixed (locked at quote time) or floating (final amount set at execution).
    /// Clients that don't implement this are treated as "float" — the norm for
    /// instant swaps and what the site disclosure already states.
    /// Implement on a client and return RateTypes.Fixed for fixed-rate quotes.
    /// </summary>
    public interface IRateType
    {
        /// <summary>"float" or "fixed" — use the RateTypes constants.</summary>
        public string RateType { get; }
    }

    public static class RateTypes
    {
        public const string Float = "float";
        public const string Fixed = "fixed";
    }
}
