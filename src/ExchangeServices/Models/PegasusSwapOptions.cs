using System;
using System.Collections.Generic;
using System.Text;

namespace ExchangeServices.Models
{

    public sealed class PegasusSwapOptions
    {
        public string SiteUrl { get; set; }
        public string SiteName { get; set; }
        public string BaseUrl { get; set; } = "https://api.pegasusswap.com";
        public int TimeoutSeconds { get; set; } = 10;
        public string UserAgent { get; set; } = "CryptoPriceNow/1.0";

        public string PublicKey { get; set; } = "";
        public string Secret { get; set; } = "";
        public char PrivacyLevel { get; set; }
    }
}
