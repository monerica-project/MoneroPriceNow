namespace ExchangeServices.Models;

public sealed class BaltexOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
 
    public string BaseUrl { get; set; } = "https://api.baltex.io";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";

    // header: x-api-key
    public string ApiKey { get; set; } = "";
    public char PrivacyLevel { get; set; }
}