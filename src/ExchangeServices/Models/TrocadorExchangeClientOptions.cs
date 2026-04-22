namespace ExchangeServices.Implementations;

public sealed class TrocadorOptions
{
    public string BaseUrl { get; set; } = "https://api.trocador.app";
    public string SiteName { get; set; } = "Trocador";
    public string? SiteUrl { get; set; } = "https://trocador.app";
    public int RequestTimeoutSeconds { get; set; } = 12;

    /// <summary>
    /// Partner API key — passed as the 'API-Key' request header.
    /// Obtain at https://trocador.app/en/affiliate/
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// USDT network used for both buy and sell quotes.
    /// Defaults to TRC20 (lowest fees, widest provider coverage on Trocador).
    /// </summary>
    public string UsdtNetwork { get; set; } = "TRC20";

    /// <summary>
    /// Reference USDT amount used when fetching buy quotes (USDT → XMR).
    /// buyPrice = BuyReferenceAmountUsdt / amount_to.
    /// A larger value avoids sub-minimum rejections from some providers.
    /// </summary>
    public decimal BuyReferenceAmountUsdt { get; set; } = 100m;

    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}