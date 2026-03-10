namespace ExchangeServices.Models;

public sealed class XgramOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://xgram.io/api/v1";
    public string ApiKey { get; set; } = "";              // required
    public int RequestTimeoutSeconds { get; set; } = 10;  // per request
    public char PrivacyLevel { get; set; }
}