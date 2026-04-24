# MoneroPriceNow

Live Monero price aggregator pulling rates from a large set of no-KYC exchanges and swap aggregators.

## Prerequisites

- .NET SDK (matching the version specified in `CryptoPriceNow.Web.csproj`)
- PowerShell (for deployment)
- Web Deploy (`msdeploy`) — invoked via the repo's `ci.bat`
- API keys / referral IDs for any exchanges you want to enable

## Local Setup

### 1. Clone the repository

```bash
git clone https://github.com/YOURUSER/CryptoPriceNow.git
cd CryptoPriceNow
```

### 2. Create `appsettings.json`

Create an `appsettings.json` file in the `CryptoPriceNow.Web` folder using the format below. Fill in API keys, referral links, and any values you need to customize per-exchange. Exchanges with blank `ApiKey` / `SiteUrl` values will need those filled in before they'll return live rates.

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Sponsors": {
    "SourceUrl": "https://app.monerica.com/sponsoredlisting/activesponsorjson",
    "CacheTtlMinutes": 5
  },
  "DisableHttpsRedirect": true,
  "TorUrl": "",
  "PriceService": {
    "WarmIntervalSeconds": 15,
    "PriceCacheSeconds": 600,
    "CurrenciesCacheMinutes": 60
  },

  "SageSwap": {
    "SiteName": "SageSwap",
    "SiteUrl": "https://sageswap.io/?utm_source=s4f595TMvM",
    "BaseUrl": "https://sageswap.io/api",
    "TimeoutSeconds": 10,
    "UserAgent": "CryptoPriceNow/1.0",
    "Token": "",
    "PrivacyLevel": "A",
    "MinAmountUsd": 10.00
  },
  "PegasusSwap": {
    "SiteName": "PegasusSwap",
    "SiteUrl": "https://pegasusswap.com/?ref=JVN3MSB",
    "BaseUrl": "https://api.pegasusswap.com",
    "TimeoutSeconds": 10,
    "UserAgent": "CryptoPriceNow/1.0",
    "PublicKey": "",
    "Secret": "",
    "PrivacyLevel": "B",
    "MinAmountUsd": 10.00
  },
  "StealthEx": {
    "SiteName": "StealthEX",
    "SiteUrl": "https://stealthex.io/?ref=IFobWnnMRU",
    "BaseUrl": "https://api.stealthex.io",
    "TimeoutSeconds": 30,
    "UserAgent": "CryptoPriceNow/1.0",
    "ApiKey": "",
    "PrivacyLevel": "B",
    "MinAmountUsd": 17.80
  },
  "Baltex": {
    "SiteName": "Baltex",
    "SiteUrl": "https://baltex.io?_bpLink=7f3fa48c-ad72405c2c024ae7",
    "BaseUrl": "https://api.baltex.io",
    "stealthex": null,
    "TimeoutSeconds": 10,
    "UserAgent": "CryptoPriceNow/1.0",
    "ApiKey": "",
    "PrivacyLevel": "B",
    "MinAmountUsd": 17.83
  },
  "OctoSwap": {
    "SiteName": "OctoSwap",
    "SiteUrl": "https://www.octoswap.io/",
    "BaseUrl": "https://api.octoswap.io/api/",
    "TimeoutSeconds": 10,
    "UserAgent": "CryptoPriceNow/1.0",
    "ApiKey": "",
    "PrivacyLevel": "A",
    "MinAmountUsd": 7000.00
  },
  "XChange": {
    "SiteName": "",
    "SiteUrl": "",
    "BaseUrl": "https://xchange.me/api/v1",
    "TimeoutSeconds": 10,
    "UserAgent": "CryptoPriceNow/1.0",
    "PrivacyLevel": "A",
    "MinAmountUsd": 50.00
  },
  "DevilExchange": {
    "SiteName": "Devil.Exchange",
    "SiteUrl": "https://devil.exchange/?ref=monerica",
    "BaseUrl": "https://devil.exchange",
    "PairsCacheSeconds": 300,
    "QuoteCacheSeconds": 10,
    "RateType": "floating",
    "RequestTimeoutSeconds": 12,
    "RetryCount": 2,
    "UserAgent": "CryptoPriceNow/1.0",
    "PrivacyLevel": "B",
    "MinAmountUsd": 50.00
  },
  "Nanswap": {
    "SiteName": "",
    "SiteUrl": "",
    "BaseUrl": "https://api.nanswap.com",
    "TimeoutSeconds": 12,
    "UserAgent": "CryptoPriceNow/1.0",
    "MinAmountUsd": 50.00
  },
  "EtzSwap": {
    "SiteName": "ETZ-Swap",
    "SiteUrl": "https://etz-swap.com?ref=RIEKD1MGVQT3KG5H0DSQ",
    "BaseUrl": "https://api.etz-swap.com",
    "TimeoutSeconds": 20,
    "ApiKey": "",
    "ApiSecretKey": "",
    "ApiKeyVersion": "2",
    "PrivacyLevel": "B",
    "MinAmountUsd": 65.28
  },
  "FuguSwap": {
    "SiteName": "FuguSwap",
    "SiteUrl": "https://fuguswap.com/?referral_id=dcc7415a701f",
    "BaseUrl": "https://api.fuguswap.com/partners",
    "TimeoutSeconds": 20,
    "ApiKey": "",
    "PrivacyLevel": "C",
    "MinAmountUsd": 100
  },
  "FixedFloat": {
    "SiteName": "FixedFloat",
    "SiteUrl": "https://fixedfloat.com/XMR/USDT/?ref=j5jktpac",
    "BaseUrl": "https://ff.io",
    "TimeoutSeconds": 20,
    "ApiKey": "",
    "ApiSecret": "",
    "PrivacyLevel": "C",
    "MinAmountUsd": 66.41
  },
  "CCECash": {
    "SiteName": "CCE Cash",
    "SiteUrl": "https://cce.cash?ref=R2sOPwHR&fromCoin=XMR|Monero&toCoin=USDT|TRON",
    "BaseUrl": "https://cce.cash",
    "ApiKey": "",
    "ApiSecret": "",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "B",
    "MinAmountUsd": 11.77
  },
  "Xgram": {
    "SiteName": "Xgram",
    "SiteUrl": "https://xgram.io/?refId=699f6dd81d1b4",
    "BaseUrl": "https://xgram.io/api/v1",
    "ApiKey": "",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "B",
    "MinAmountUsd": 50.00
  },
  "LetsExchange": {
    "SiteName": "LetsExchange",
    "SiteUrl": "https://letsexchange.io/?ref_id=fEKMDDWtsu9tN98X",
    "BaseUrl": "https://api.letsexchange.io/api",
    "ApiKey": "",
    "AffiliateId": "",
    "UseFloatRate": true,
    "RequestTimeoutSeconds": 12,
    "RetryCount": 2,
    "UserAgent": "CryptoPriceNow/1.0",
    "PrivacyLevel": "C",
    "MinAmountUsd": 140.00
  },
  "Wagyu": {
    "SiteName": "",
    "SiteUrl": "",
    "BaseUrl": "https://api.wagyu.xyz",
    "UserAgent": "MonericaPriceBot/1.0",
    "RatesCacheSeconds": 25,
    "RequestTimeoutSeconds": 8,
    "RetryCount": 2,
    "PrivacyLevel": "A",
    "MinAmountUsd": 50.00
  },
  "Exolix": {
    "SiteName": "Exolix",
    "SiteUrl": "https://exolix.com?ref=BD69BCE01E85280E4179278A0953E133",
    "BaseUrl": "https://exolix.com/api/v2",
    "ApiKey": "",
    "RequestTimeoutSeconds": 20,
    "RetryCount": 2,
    "RateType": "float",
    "UserAgent": "Monerica/1.0 (+https://monerica.com)",
    "PrivacyLevel": "B",
    "MinAmountUsd": 49.00
  },
  "ChangeNow": {
    "SiteName": "ChangeNOW",
    "SiteUrl": "https://changenow.app.link/referral?link_id=40676d9d377a6b&from=xmr&to=usdttrc20",
    "BaseUrl": "https://api.changenow.io",
    "ApiKey": "",
    "Flow": "standard",
    "CurrenciesCacheSeconds": 21600,
    "RequestTimeoutSeconds": 20,
    "RetryCount": 2,
    "UserAgent": "Monerica/1.0",
    "PrivacyLevel": "C",
    "MinAmountUsd": 17.81
  },
  "WizardSwap": {
    "SiteName": "",
    "SiteUrl": "",
    "BaseUrl": "https://www.wizardswap.io",
    "ApiKey": "",
    "RequestTimeoutSeconds": 20,
    "RetryCount": 2,
    "CurrenciesCacheSeconds": 21600,
    "UserAgent": "Monerica/1.0",
    "PrivacyLevel": "B",
    "MinAmountUsd": 50.00
  },
  "Swapuz": {
    "BaseUrl": "https://api.swapuz.com",
    "ApiKey": "",
    "SiteName": "Swapuz",
    "SiteUrl": "https://swapuz.com/?ref=8bb61963-e9c2-48c9-9aa3-c59c9c6da3af",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "A"
  },
  "Changee": {
    "BaseUrl": "https://changee.com",
    "ApiKey": "",
    "SiteName": "Changee",
    "SiteUrl": "https://changee.com?refId=671906a166318",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "C",
    "MinAmountUsd": 500.00
  },
  "Quickex": {
    "BaseUrl": "https://quickex.io/",
    "SiteName": "Quickex",
    "SiteUrl": "https://quickex.io/exchange-usdttrc20-xmr?ref=aff_1089",
    "PrivacyLevel": "C",
    "RequestTimeoutSeconds": 10,
    "ReferrerId": "",
    "XmrCurrency": "XMR",
    "XmrNetwork": "XMR",
    "UsdtCurrency": "USDT",
    "UsdtNetwork": "TRC20",
    "SellProbeAmountXmr": 2,
    "BuyProbeAmountUsdt": 200,
    "MinAmountUsd": 50.00
  },
  "SimpleSwap": {
    "SiteName": "SimpleSwap",
    "BaseUrl": "https://api.simpleswap.io",
    "ApiKey": "",
    "SiteUrl": "https://simpleswap.io/?ref=f3b46b88d743&from=xmr-xmr&to=usdt-trx&amount=1",
    "PrivacyLevel": "C",
    "MinAmountUsd": 19.90
  },
  "GoDex": {
    "BaseUrl": "https://api.godex.io",
    "ApiKey": "",
    "AffiliateId": "IWqW8MF0X29Ridbv",
    "SiteName": "GoDex",
    "SiteUrl": "https://godex.io/?aff_id=IWqW8MF0X29Ridbv&utm_source=affiliate&utm_medium=monerica&utm_campaign=IWqW8MF0X29Ridbv",
    "RequestTimeoutSeconds": 12,
    "PrivacyLevel": "C",
    "MinAmountUsd": 160
  },
  "BitcoinVN": {
    "BaseUrl": "https://bitcoinvn.io",
    "ApiKey": "",
    "SiteName": "BitcoinVN",
    "SiteUrl": "https://bitcoinvn.io/?ref=81883534a8a05e53",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "B",
    "MinAmountUsd": 37.87962
  },
  "AlfaCash": {
    "BaseUrl": "https://www.alfa.cash",
    "SiteName": "AlfaCash",
    "SiteUrl": "https://www.alfa.cash/?rid=2f7aec12",
    "RequestTimeoutSeconds": 12,
    "PrivacyLevel": "B",
    "MinAmountUsd": 23.44
  },
  "SecureShift": {
    "ApiKey": "",
    "BaseUrl": "https://secureshift.io/api/v3/",
    "SiteName": "SecureShift",
    "SiteUrl": "https://secureshift.io",
    "PrivacyLevel": "B",
    "RequestTimeoutSeconds": 10,
    "XmrSymbol": "xmr",
    "XmrNetwork": "xmr",
    "UsdtSymbol": "usdt",
    "UsdtNetwork": "trc20",
    "BuyProbeAmountUsdt": 100,
    "MinAmountUsd": 50.00
  },
  "Swapgate": {
    "BaseUrl": "https://swapgate.io/",
    "SiteName": "Swapgate",
    "SiteUrl": "https://swapgate.io/exchange-USDTERC20-XMR?ref=aff_831",
    "PrivacyLevel": "C",
    "RequestTimeoutSeconds": 10,
    "XmrCurrency": "XMR",
    "XmrNetwork": "XMR",
    "UsdtCurrency": "USDT",
    "UsdtNetwork": "TRC20",
    "SellProbeAmountXmr": 2,
    "BuyProbeAmountUsdt": 100,
    "MinAmountUsd": 110.00
  },
  "ChangeHero": {
    "ApiKey": "",
    "BaseUrl": "https://api.changehero.io/v2/",
    "SiteName": "ChangeHero",
    "SiteUrl": "https://changehero.io/?ref=428fdd2d707649b6b85c4a86d1230c52",
    "PrivacyLevel": "C",
    "RequestTimeoutSeconds": 10,
    "XmrCode": "xmr",
    "UsdtCode": "usdt20",
    "BuyProbeAmountUsdt": 100,
    "MinAmountUsd": 50.00
  },
  "Swapter": {
    "ApiKey": "",
    "BaseUrl": "https://api.swapter.io/",
    "SiteName": "Swapter",
    "SiteUrl": "https://swapter.io/?ref=6cb8e05c-4b4f-4b49-a0da-8bd0ad58001b",
    "PrivacyLevel": "C",
    "RequestTimeoutSeconds": 10,
    "XmrCoin": "XMR",
    "XmrNetwork": "XMR",
    "UsdtCoin": "USDT",
    "UsdtNetwork": "TRX",
    "BuyProbeAmountUsdt": 100,
    "MinAmountUsd": 23.05
  },
  "BitXChange": {
    "ApiKey": "",
    "BaseUrl": "https://api.bitxchange.io",
    "SiteName": "BitXChange",
    "SiteUrl": "https://www.bitxchange.io/?ref=vqpQnABudv",
    "PrivacyLevel": "C",
    "RequestTimeoutSeconds": 10,
    "XmrSymbol": "XMR",
    "XmrNetwork": "XMR",
    "UsdtSymbol": "USDT",
    "UsdtNetwork": "TRC20",
    "BuyProbeAmountUsdt": 100,
    "MinAmountUsd": 100.00
  },
  "CypherGoat": {
    "ApiKey": "",
    "BaseUrl": "https://api.cyphergoat.com",
    "SiteName": "CypherGoat",
    "SiteUrl": "https://cyphergoat.com",
    "PrivacyLevel": "V",
    "RequestTimeoutSeconds": 10,
    "XmrCoin": "xmr",
    "XmrNetwork": "xmr",
    "UsdtCoin": "usdt",
    "UsdtNetwork": "tron",
    "BuyProbeAmountUsdt": 200,
    "MinAmountUsd": 4.00
  },
  "StereoSwap": {
    "ApiKey": "",
    "BaseUrl": "https://api.stereoswap.app",
    "SiteName": "StereoSwap",
    "SiteUrl": "https://stereoswap.app?referral_code=ATQ9bpXd",
    "PrivacyLevel": "B",
    "RequestTimeoutSeconds": 10,
    "TypeSwap": 2,
    "Mode": "standard",
    "XmrCoin": "XMR",
    "XmrNetwork": "XMR",
    "UsdtCoin": "USDT",
    "UsdtNetwork": "TRX",
    "BuyProbeAmountUsdt": 100,
    "MinAmountUsd": 10.00
  },
  "Trocador": {
    "BaseUrl": "https://api.trocador.app",
    "SiteName": "Trocador",
    "SiteUrl": "https://trocador.app/en/?network_to=TRC20&ticker_from=xmr&network_from=monero&amount_from=1&ticker_to=usdt&ref=QOAQCBOx11",
    "RequestTimeoutSeconds": 12,
    "ApiKey": "",
    "UsdtNetwork": "TRC20",
    "BuyReferenceAmountUsdt": 100,
    "PrivacyLevel": "V",
    "MinAmountUsd": 4.53
  }
}
```

Common per-exchange fields:

- **SiteName / SiteUrl** — display name and referral link shown in the UI
- **BaseUrl** — API root for the exchange client
- **ApiKey / ApiSecret / Token / PublicKey** — credentials where the exchange requires them
- **PrivacyLevel** — internal grading (A / B / C / V) surfaced in the UI
- **MinAmountUsd** — minimum swap amount enforced by the exchange, used to filter quotes
- **TimeoutSeconds / RequestTimeoutSeconds / RetryCount** — HTTP client tuning

> **Do not commit this file.** Add it to `.gitignore`.

### 3. Run the application

```bash
dotnet run --project CryptoPriceNow.Web/CryptoPriceNow.Web.csproj
```

## Deployment

Deployment uses Web Deploy (`msdeploy`) and is driven by the repo's `ci.bat`. A thin PowerShell wrapper holds your per-environment values and calls `ci.bat DeployWebApp`.

### Create `cryptopricenow_deployment.ps1`

Create the script (wherever you keep local deploy scripts) with the following contents, filling in every value:

```powershell
$MsDeployLocation  = "https://HOST:PORT"
$webAppHost        = "WEBAPP"
$contentPathDes    = "FILEPATH"
$msDeployUserName  = 'USERNAME'
$msDeployPassword  = 'PASSWORD'
$dbConnectionString = 'Data Source=HOST,PORT;Initial Catalog=DBNAME;User ID=USERNAME;Password=PASSWORD;TrustServerCertificate=true'

cd "FILEPATHOFREPO"

.\ci.bat DeployWebApp -properties "@{'MsDeployLocation'='$MsDeployLocation';'webAppHost'='$webAppHost';'contentPathDes'='$contentPathDes';'msDeployUserName'='$msDeployUserName';'msDeployPassword'='$msDeployPassword';'dbConnectionString'='$dbConnectionString';}"
```

Field reference:

- **MsDeployLocation** — Web Deploy endpoint URL (e.g. `https://yourserver:8172`)
- **webAppHost** — IIS site name / host header the deploy targets
- **contentPathDes** — destination content path on the server (the IIS physical path)
- **msDeployUserName / msDeployPassword** — Web Deploy credentials
- **dbConnectionString** — production SQL connection string
- **FILEPATHOFREPO** — local path to the cloned repo (so `ci.bat` resolves correctly)

> **Do not commit `cryptopricenow_deployment.ps1`.** It contains plaintext credentials.

### Run the deployment

From PowerShell:

```powershell
.\cryptopricenow_deployment.ps1
```

This will `cd` into the repo and invoke `ci.bat DeployWebApp` with the property bag above, which handles build, package, and Web Deploy publish to the target server.

## .gitignore

Make sure at minimum the following are ignored:

```
**/appsettings.json
**/appsettings.Production.json
cryptopricenow_deployment.ps1
```

## License

See `LICENSE` file.
