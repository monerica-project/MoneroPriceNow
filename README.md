# MoneroPriceNow

Live Monero price aggregator pulling rates from a large set of no-KYC exchanges and swap aggregators.

## Prerequisites

### For local development

- .NET SDK (matching the version specified in `CryptoPriceNow.Web.csproj`)
- A copy of `appsettings.json` populated with API keys and referral IDs (see below)

### For deployment to a Linux VPS

- An Ubuntu 22.04+ VPS with the base stack installed (Docker, .NET ASP.NET runtime, nginx, certbot, ufw, fail2ban, a `webapp` system user, and an `/opt/<app-name>` directory layout)
- SSH access to the VPS configured with key-based auth
- An entry in `~/.ssh/config` for the VPS so the deploy script can connect by alias
- Passwordless `sudo` configured for your VPS user (see "VPS prerequisites" below)
- DNS A record(s) for your domain pointing at the VPS public IP

## Local Setup

### 1. Clone the repository

```bash
git clone https://github.com/YOURUSER/MoneroPriceNow.git
cd MoneroPriceNow
```

### 2. Create `appsettings.json`

Create an `appsettings.json` file in the `src/CryptoPriceNow.Web` folder using the format below. Fill in API keys, referral links, and any values you need to customize per-exchange. Exchanges with blank `ApiKey` / `SiteUrl` values will need those filled in before they'll return live rates.

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
    "SourceUrl": "https://YOUR-SPONSOR-FEED-URL/activesponsorjson",
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
    "SiteUrl": "https://sageswap.io/?utm_source=YOUR_REF",
    "BaseUrl": "https://sageswap.io/api",
    "TimeoutSeconds": 10,
    "UserAgent": "CryptoPriceNow/1.0",
    "Token": "",
    "PrivacyLevel": "A",
    "MinAmountUsd": 10.00
  },
  "PegasusSwap": {
    "SiteName": "PegasusSwap",
    "SiteUrl": "https://pegasusswap.com/?ref=YOUR_REF",
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
    "SiteUrl": "https://stealthex.io/?ref=YOUR_REF",
    "BaseUrl": "https://api.stealthex.io",
    "TimeoutSeconds": 30,
    "UserAgent": "CryptoPriceNow/1.0",
    "ApiKey": "",
    "PrivacyLevel": "B",
    "MinAmountUsd": 17.80
  },
  "Baltex": {
    "SiteName": "Baltex",
    "SiteUrl": "https://baltex.io?_bpLink=YOUR_REF",
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
    "SiteUrl": "https://devil.exchange/?ref=YOUR_REF",
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
    "SiteUrl": "https://etz-swap.com?ref=YOUR_REF",
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
    "SiteUrl": "https://fuguswap.com/?referral_id=YOUR_REF",
    "BaseUrl": "https://api.fuguswap.com/partners",
    "TimeoutSeconds": 20,
    "ApiKey": "",
    "PrivacyLevel": "C",
    "MinAmountUsd": 100
  },
  "FixedFloat": {
    "SiteName": "FixedFloat",
    "SiteUrl": "https://fixedfloat.com/XMR/USDT/?ref=YOUR_REF",
    "BaseUrl": "https://ff.io",
    "TimeoutSeconds": 20,
    "ApiKey": "",
    "ApiSecret": "",
    "PrivacyLevel": "C",
    "MinAmountUsd": 66.41
  },
  "CCECash": {
    "SiteName": "CCE Cash",
    "SiteUrl": "https://cce.cash?ref=YOUR_REF&fromCoin=XMR|Monero&toCoin=USDT|TRON",
    "BaseUrl": "https://cce.cash",
    "ApiKey": "",
    "ApiSecret": "",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "B",
    "MinAmountUsd": 11.77
  },
  "Xgram": {
    "SiteName": "Xgram",
    "SiteUrl": "https://xgram.io/?refId=YOUR_REF",
    "BaseUrl": "https://xgram.io/api/v1",
    "ApiKey": "",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "B",
    "MinAmountUsd": 50.00
  },
  "LetsExchange": {
    "SiteName": "LetsExchange",
    "SiteUrl": "https://letsexchange.io/?ref_id=YOUR_REF",
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
    "UserAgent": "CryptoPriceNow/1.0",
    "RatesCacheSeconds": 25,
    "RequestTimeoutSeconds": 8,
    "RetryCount": 2,
    "PrivacyLevel": "A",
    "MinAmountUsd": 50.00
  },
  "Exolix": {
    "SiteName": "Exolix",
    "SiteUrl": "https://exolix.com?ref=YOUR_REF",
    "BaseUrl": "https://exolix.com/api/v2",
    "ApiKey": "",
    "RequestTimeoutSeconds": 20,
    "RetryCount": 2,
    "RateType": "float",
    "UserAgent": "CryptoPriceNow/1.0",
    "PrivacyLevel": "B",
    "MinAmountUsd": 49.00
  },
  "ChangeNow": {
    "SiteName": "ChangeNOW",
    "SiteUrl": "https://changenow.app.link/referral?link_id=YOUR_REF&from=xmr&to=usdttrc20",
    "BaseUrl": "https://api.changenow.io",
    "ApiKey": "",
    "Flow": "standard",
    "CurrenciesCacheSeconds": 21600,
    "RequestTimeoutSeconds": 20,
    "RetryCount": 2,
    "UserAgent": "CryptoPriceNow/1.0",
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
    "UserAgent": "CryptoPriceNow/1.0",
    "PrivacyLevel": "B",
    "MinAmountUsd": 50.00
  },
  "Swapuz": {
    "BaseUrl": "https://api.swapuz.com",
    "ApiKey": "",
    "SiteName": "Swapuz",
    "SiteUrl": "https://swapuz.com/?ref=YOUR_REF",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "A"
  },
  "Changee": {
    "BaseUrl": "https://changee.com",
    "ApiKey": "",
    "SiteName": "Changee",
    "SiteUrl": "https://changee.com?refId=YOUR_REF",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "C",
    "MinAmountUsd": 500.00
  },
  "Quickex": {
    "BaseUrl": "https://quickex.io/",
    "SiteName": "Quickex",
    "SiteUrl": "https://quickex.io/exchange-usdttrc20-xmr?ref=YOUR_REF",
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
    "SiteUrl": "https://simpleswap.io/?ref=YOUR_REF&from=xmr-xmr&to=usdt-trx&amount=1",
    "PrivacyLevel": "C",
    "MinAmountUsd": 19.90
  },
  "GoDex": {
    "BaseUrl": "https://api.godex.io",
    "ApiKey": "",
    "AffiliateId": "YOUR_AFFILIATE_ID",
    "SiteName": "GoDex",
    "SiteUrl": "https://godex.io/?aff_id=YOUR_AFFILIATE_ID",
    "RequestTimeoutSeconds": 12,
    "PrivacyLevel": "C",
    "MinAmountUsd": 160
  },
  "BitcoinVN": {
    "BaseUrl": "https://bitcoinvn.io",
    "ApiKey": "",
    "SiteName": "BitcoinVN",
    "SiteUrl": "https://bitcoinvn.io/?ref=YOUR_REF",
    "RequestTimeoutSeconds": 10,
    "PrivacyLevel": "B",
    "MinAmountUsd": 37.87962
  },
  "AlfaCash": {
    "BaseUrl": "https://www.alfa.cash",
    "SiteName": "AlfaCash",
    "SiteUrl": "https://www.alfa.cash/?rid=YOUR_REF",
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
    "SiteUrl": "https://swapgate.io/exchange-USDTERC20-XMR?ref=YOUR_REF",
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
    "SiteUrl": "https://changehero.io/?ref=YOUR_REF",
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
    "SiteUrl": "https://swapter.io/?ref=YOUR_REF",
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
    "SiteUrl": "https://www.bitxchange.io/?ref=YOUR_REF",
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
    "SiteUrl": "https://stereoswap.app?referral_code=YOUR_REF",
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
    "SiteUrl": "https://trocador.app/en/?network_to=TRC20&ticker_from=xmr&network_from=monero&amount_from=1&ticker_to=usdt&ref=YOUR_REF",
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

> **Do not commit this file.** It contains your real API keys and is git-ignored.

### 3. Run the application locally

```bash
dotnet run --project src/CryptoPriceNow.Web/CryptoPriceNow.Web.csproj
```

## Deployment

Deployment uses a Bash script that runs from your laptop, builds the project locally, syncs the binaries to the VPS via rsync, installs/updates the systemd unit and nginx config, ensures TLS via Let's Encrypt, and (optionally) configures a Tor hidden service.

The pipeline targets a Linux VPS running Kestrel behind nginx, managed by systemd. It replaces the older Windows + IIS + MSDeploy setup.

### Repository layout

```
MoneroPriceNow/
├── src/
│   └── CryptoPriceNow.Web/
│       └── CryptoPriceNow.Web.csproj
└── CI/
    ├── deploy.sh              # main deploy orchestrator (runs from your laptop)
    └── deploy-config.sh       # per-environment values, edit before deploying
```

### VPS prerequisites (one-time, before first deploy)

These steps assume your VPS is already running Ubuntu 22.04+ with the base stack installed (Docker, .NET ASP.NET runtime, nginx, certbot, ufw, fail2ban, a `webapp` system user, and the `/opt/<APP_NAME>` directory layout).

**1. Configure an SSH alias on your laptop.** Add to `~/.ssh/config`:

```
Host my-vps
    HostName YOUR.VPS.IP.ADDRESS
    Port 22
    User YOUR_USER
```

Replace with your VPS's IP, SSH port, and admin user. Test with `ssh my-vps`.

**2. Configure passwordless `sudo` for the deploy user on the VPS.** The deploy script invokes `sudo` over SSH for things like writing systemd units, reloading nginx, and running certbot. Without NOPASSWD, every step would prompt for a password and the non-interactive SSH would hang.

```bash
ssh my-vps
echo "YOUR_USER ALL=(ALL) NOPASSWD: ALL" | sudo tee /etc/sudoers.d/YOUR_USER-deploy
sudo chmod 440 /etc/sudoers.d/YOUR_USER-deploy
exit
```

**3. Point DNS at the VPS.** Create A records for your domain pointing at the VPS IP:

| Type | Name | Value |
|------|------|-------|
| A | yourdomain.com | YOUR.VPS.IP.ADDRESS |
| CNAME | www | yourdomain.com |

**4. (Optional) Tor hidden service keys.** If you want to preserve an existing `.onion` address from a prior server, copy the three files from the old `<tor-data-dir>/<service-name>/` folder:

- `hostname`
- `hs_ed25519_public_key`
- `hs_ed25519_secret_key`

Save them in a folder on your laptop (e.g. `~/tor-keys/myapp/`) and point `TOR_KEYS_DIR` at it in the deploy config below.

The deploy script will install Tor on the VPS, copy the keys into `/var/lib/tor/<APP_NAME>/` with correct ownership and permissions, configure `torrc`, and start the service. Subsequent deploys detect that Tor is already configured and skip the setup so the onion stays continuously available.

If you don't want a Tor hidden service, leave `TOR_KEYS_DIR=""` and the entire Tor block is skipped.

### Configure the deploy

Edit `CI/deploy-config.sh` with values for your environment:

```bash
#!/usr/bin/env bash
# deploy-config.sh — sourced by deploy.sh

# Connection (uses ~/.ssh/config alias for port + user)
VPS="my-vps"

# App identity — drives systemd unit name, nginx conf name, and deploy path
APP_NAME="myapp"
DOMAIN="yourdomain.com"
APP_PORT=5085

# Project paths
WEB_PROJECT="../src/CryptoPriceNow.Web/CryptoPriceNow.Web.csproj"
DEPLOY_PATH="/opt/$APP_NAME"

# Service user that owns and runs the app on the VPS
SERVICE_USER="webapp"

# Tor hidden service. Leave empty to skip Tor entirely.
TOR_KEYS_DIR=""

# Smoke test (overridden automatically based on whether TLS is configured)
REQUEST_PROTOCOL="https://"
```

Field reference:

- **VPS** — SSH alias from `~/.ssh/config`
- **APP_NAME** — used for systemd unit name, nginx conf filename, and `/opt/<APP_NAME>` deploy directory. Must be unique per app on the VPS so multiple apps can coexist
- **DOMAIN** — public-facing domain. For an apex domain (e.g. `yourdomain.com`), the script automatically includes `www.<domain>` in the nginx server_name and TLS cert. For subdomains (e.g. `dev.yourdomain.com`) it does not
- **APP_PORT** — port Kestrel binds to on `127.0.0.1`. Apps on the same VPS need different ports
- **WEB_PROJECT** — path to the `.csproj` to publish. Relative to the CI folder, or absolute
- **DEPLOY_PATH** — destination directory on the VPS
- **SERVICE_USER** — system user that owns the deployed binaries and runs the systemd service. Must already exist on the VPS
- **TOR_KEYS_DIR** — path on your laptop to the folder containing the three Tor key files. Set to `""` to skip Tor

### Run the deploy

```bash
cd CI
./deploy.sh
```

The script:

1. Cleans `bin/`, `obj/`, and `publish/` folders
2. Runs `dotnet publish` for `linux-x64` (framework-dependent)
3. Rsyncs binaries to `/opt/<APP_NAME>/` on the VPS, preserving an existing `appsettings.Production.json` if one exists there
4. Installs/refreshes the systemd unit and restarts the service
5. (If `TOR_KEYS_DIR` is set) ensures Tor is installed, keys are in place, `torrc` is configured, and Tor is running. Skipped on subsequent deploys when nothing has changed
6. Writes the nginx site config — including the `Onion-Location` header if a Tor onion is configured — and reloads nginx
7. Ensures TLS via certbot. Issues a new cert on first run; on subsequent runs, reattaches the existing cert to the rewritten nginx config without requesting a new one
8. Runs a smoke test (HTTP or HTTPS depending on whether the cert is in place)

### Flags

```bash
./deploy.sh --skip-build   # skip dotnet publish (reuse last publish output)
./deploy.sh --no-ssl       # skip TLS (useful before DNS is pointed)
./deploy.sh                # default — full pipeline including TLS
```

### Daily workflow

For routine code changes:

```bash
# Edit code, then:
cd CI
./deploy.sh
```

Total time is usually 30–60 seconds. Verify in your browser, hard-refresh if needed.

### Useful one-liners

```bash
# Tail live application logs
ssh my-vps "sudo journalctl -u myapp -f"

# Service status
ssh my-vps "sudo systemctl status myapp --no-pager"

# Manual restart (deploy.sh does this for you, rarely needed)
ssh my-vps "sudo systemctl restart myapp"

# Tail nginx access log
ssh my-vps "sudo tail -f /var/log/nginx/access.log"

# Verify the .onion address being served (if Tor is configured)
ssh my-vps "sudo cat /var/lib/tor/myapp/hostname"

# Disk usage on the VPS
ssh my-vps "df -h /"
```

### TLS auto-renewal

certbot installs a systemd timer that auto-renews certs within 30 days of expiry. Verify it's enabled:

```bash
ssh my-vps "sudo systemctl list-timers | grep -i certbot"
```

No manual renewal action needed.

## .gitignore

Make sure at minimum the following are ignored:

```
**/appsettings.json
**/appsettings.Production.json
**/bin/
**/obj/
publish/
CI/deploy-config.sh
```

`deploy-config.sh` is excluded because it contains environment-specific values (your VPS hostname/IP, paths, etc).

## License

See `LICENSE` file.
