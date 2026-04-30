# deploy.ps1
# Run from the CI folder:  .\deploy.ps1
#
# Flags:
#   -SkipBuild   skip dotnet publish
#   -SSL         install Let's Encrypt SSL after deploy
#   -Tor         set up Tor hidden service

param(
    [switch]$SkipBuild,
    [switch]$SSL,
    [switch]$Tor
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# -- Load config --------------------------------------------------------------
$configPath = Join-Path $PSScriptRoot "deploy-config.ps1"
if (-not (Test-Path $configPath)) {
    Write-Error "deploy-config.ps1 not found next to deploy.ps1"
    exit 1
}
. $configPath

# -- Helpers ------------------------------------------------------------------
function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    WARN: $msg" -ForegroundColor Yellow }

function SSH($cmd) {
    & $PLINK -ssh -pw $SSH_PASSWORD -batch "$SSH_USER@$SSH_HOST" $cmd
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SSH command failed: $cmd"
        exit 1
    }
}

function SSH-Ignore($cmd) {
    & $PLINK -ssh -pw $SSH_PASSWORD -batch "$SSH_USER@$SSH_HOST" $cmd
}

function SSH-Query($cmd) {
    return (& $PLINK -ssh -pw $SSH_PASSWORD -batch `
        "$SSH_USER@$SSH_HOST" $cmd 2>$null | Out-String).Trim()
}

function SCP($local, $remote) {
    & $PSCP -pw $SSH_PASSWORD -r -batch $local "${SSH_USER}@${SSH_HOST}:${remote}"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "SCP failed: $local -> $remote"
        exit 1
    }
}

function Save-UnixFile([string]$path, [string]$content) {
    $clean = $content -replace "`r`n", "`n" -replace "`r", "`n"
    [System.IO.File]::WriteAllText(
        $path, $clean, [System.Text.UTF8Encoding]::new($false))
}

function Run-RemoteScript([string]$localPath, [string]$remotePath) {
    SCP $localPath $remotePath
    SSH "chmod +x $remotePath && bash $remotePath"
}

# -- Maintenance page ---------------------------------------------------------
$MaintenanceHtml = @'
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <meta http-equiv="refresh" content="15">
  <title>Updating - CryptoPriceNow</title>
  <style>
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      min-height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      background: #0f0f0f;
      color: #e0e0e0;
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    .card { text-align: center; padding: 3rem 2.5rem; max-width: 440px; }
    .icon {
      font-size: 2.8rem;
      margin-bottom: 1.25rem;
      display: inline-block;
      animation: spin 3s linear infinite;
    }
    @keyframes spin {
      from { transform: rotate(0deg); }
      to   { transform: rotate(360deg); }
    }
    h1 { font-size: 1.5rem; font-weight: 600; margin-bottom: 0.75rem; color: #f7931a; }
    p  { font-size: 0.95rem; line-height: 1.6; color: #aaa; }
    .note { margin-top: 1.75rem; font-size: 0.8rem; color: #555; }
  </style>
</head>
<body>
  <div class="card">
    <div class="icon">&#9881;</div>
    <h1>Updating in progress</h1>
    <p>MoneroPriceNow is being updated and will be back shortly.</p>
    <p class="note">This page refreshes automatically every 15 seconds.</p>
  </div>
</body>
</html>
'@

function Enable-MaintenancePage {
    Write-Step "Enabling maintenance page"

    $htmlFile = Join-Path $env:TEMP "maintenance.html"
    Save-UnixFile $htmlFile $MaintenanceHtml
    SCP $htmlFile "/tmp/maintenance.html"
    SSH "docker exec nginx mkdir -p /var/www"
    SSH "docker cp /tmp/maintenance.html nginx:/var/www/maintenance.html"

    $certNow = SSH-Query "test -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem && echo yes || echo no"

    if ($certNow -eq "yes") {
        $mConf  = "server {`n"
        $mConf += "    listen 80;`n"
        $mConf += "    server_name $DOMAIN www.$DOMAIN;`n"
        $mConf += "    return 301 https://`$host`$request_uri;`n"
        $mConf += "}`n`n"
        $mConf += "server {`n"
        $mConf += "    listen 443 ssl;`n"
        $mConf += "    server_name $DOMAIN www.$DOMAIN;`n"
        $mConf += "    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;`n"
        $mConf += "    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;`n"
        $mConf += "    ssl_protocols TLSv1.2 TLSv1.3;`n"
        $mConf += "    ssl_ciphers HIGH:!aNULL:!MD5;`n"
        $mConf += "    location / {`n"
        $mConf += "        root /var/www;`n"
        $mConf += "        try_files /maintenance.html =503;`n"
        $mConf += "        add_header Retry-After 30;`n"
        $mConf += "    }`n"
        $mConf += "}`n"
    } else {
        $mConf  = "server {`n"
        $mConf += "    listen 80;`n"
        $mConf += "    server_name $DOMAIN www.$DOMAIN;`n"
        $mConf += "    location / {`n"
        $mConf += "        root /var/www;`n"
        $mConf += "        try_files /maintenance.html =503;`n"
        $mConf += "        add_header Retry-After 30;`n"
        $mConf += "    }`n"
        $mConf += "}`n"
    }

    $mFile = Join-Path $env:TEMP "$APP_NAME.maint.conf"
    Save-UnixFile $mFile $mConf
    SCP $mFile "/tmp/$APP_NAME.conf"
    SSH "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    SSH "docker exec nginx nginx -s reload"
    Write-Ok "Maintenance page live"
}

function Wait-ForApp {
    Write-Step "Waiting for app to become healthy"
    $maxAttempts = 24   # 2 min total
    $attempt = 0
    $healthy = $false
    $lastStatus = ""
    while ($attempt -lt $maxAttempts) {
        $attempt++
        $lastStatus = SSH-Query "curl -sL -o /dev/null -w '%{http_code}' http://localhost:$APP_PORT/ 2>/dev/null || echo 000"
        if ($lastStatus -eq "200") { $healthy = $true; break }
        Write-Host "    Attempt $attempt/$maxAttempts - HTTP $lastStatus, retrying in 5s..." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    }
    if (-not $healthy) {
        Write-Host ""
        Write-Host "    App did not respond with HTTP 200 after $maxAttempts attempts." -ForegroundColor Red
        Write-Host "    Last status: HTTP $lastStatus" -ForegroundColor Red
        Write-Host "    Check logs:  journalctl -u $APP_NAME -n 80 --no-pager" -ForegroundColor Red
        Write-Error "Deployment aborted - app unhealthy"
        exit 1
    }
    Write-Ok "App is healthy (HTTP 200)"
}

# -- Step 1: Build ------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Step "Building web app (linux-x64)"
    $webOut = Join-Path $PSScriptRoot "..\publish\web"
    if (Test-Path $webOut) { Remove-Item $webOut -Recurse -Force }

    dotnet publish $WEB_PROJECT `
        -c Release `
        -r linux-x64 `
        --self-contained false `
        -o $webOut

    if ($LASTEXITCODE -ne 0) { Write-Error "Web build failed"; exit 1 }
    Write-Ok "Web app built at $webOut"
}

# -- Step 2: Bootstrap server (idempotent) -----------------------------------
Write-Step "Bootstrapping server"

SSH "apt-get update -q"
SSH "command -v dotnet > /dev/null 2>&1 || (wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/ms.deb && dpkg -i /tmp/ms.deb && apt-get update -q && apt-get install -y aspnetcore-runtime-10.0)"
SSH "mkdir -p $DEPLOY_PATH"
SSH-Ignore "ufw allow $APP_PORT/tcp 2>/dev/null || true"

Write-Ok "Server dependencies ready"
 
# -- Step 4: Deploy web app ---------------------------------------------------
Write-Step "Deploying web app"

Enable-MaintenancePage
SSH-Ignore "systemctl stop $APP_NAME 2>/dev/null || true"

$webOut = Join-Path $PSScriptRoot "..\publish\web"
$webTar = Join-Path $env:TEMP "web.tar.gz"
Push-Location $webOut
& tar -czf $webTar .
Pop-Location

SCP $webTar "/tmp/web.tar.gz"
SSH "mkdir -p $DEPLOY_PATH && tar -xzf /tmp/web.tar.gz -C $DEPLOY_PATH && rm /tmp/web.tar.gz"
SSH "chown -R www-data:www-data $DEPLOY_PATH && chmod -R 755 $DEPLOY_PATH"

# --- Write systemd unit and start service ---
$svcContent = "[Unit]`n" +
    "Description=CryptoPriceNow Web ($DOMAIN)`n" +
    "After=network.target`n" +
    "`n" +
    "[Service]`n" +
    "WorkingDirectory=$DEPLOY_PATH`n" +
    "ExecStart=/usr/bin/dotnet $DEPLOY_PATH/CryptoPriceNow.Web.dll`n" +
    "Restart=always`n" +
    "RestartSec=10`n" +
    "User=www-data`n" +
    "Environment=ASPNETCORE_ENVIRONMENT=Production`n" +
    "Environment=ASPNETCORE_URLS=http://0.0.0.0:$APP_PORT`n" +
    "Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false`n" +
    "`n" +
    "[Install]`n" +
    "WantedBy=multi-user.target`n"

$svcFile = Join-Path $env:TEMP "$APP_NAME.service"
Save-UnixFile $svcFile $svcContent
SCP $svcFile "/etc/systemd/system/$APP_NAME.service"
SSH "systemctl daemon-reload && systemctl enable $APP_NAME && systemctl restart $APP_NAME"

Wait-ForApp

Write-Ok "Web app deployed on port $APP_PORT"

# -- Step 5: Configure Nginx (restore real proxy config) ---------------------
Write-Step "Configuring Nginx"

$certExists = SSH-Query "test -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem && echo yes || echo no"

# If Tor is configured, grab the onion hostname so we can advertise it.
$onionHost = SSH-Query "cat /var/lib/tor/$APP_NAME/hostname 2>/dev/null || echo ''"
if ($onionHost) {
    Write-Host "    Advertising onion: $onionHost" -ForegroundColor Gray
}

if ($certExists -eq "yes") {
    SSH "cp -L /etc/letsencrypt/live/$DOMAIN/fullchain.pem /tmp/fullchain.pem && cp -L /etc/letsencrypt/live/$DOMAIN/privkey.pem /tmp/privkey.pem"
    SSH "docker exec nginx mkdir -p /etc/letsencrypt/live/$DOMAIN"
    SSH "docker cp /tmp/fullchain.pem nginx:/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
    SSH "docker cp /tmp/privkey.pem  nginx:/etc/letsencrypt/live/$DOMAIN/privkey.pem"

    $nginxSslConf  = "server {`n"
    $nginxSslConf += "    listen 80;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    return 301 https://`$host`$request_uri;`n"
    $nginxSslConf += "}`n`n"
    $nginxSslConf += "server {`n"
    $nginxSslConf += "    listen 443 ssl;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;`n"
    $nginxSslConf += "    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;`n"
    $nginxSslConf += "    ssl_protocols TLSv1.2 TLSv1.3;`n"
    $nginxSslConf += "    ssl_ciphers HIGH:!aNULL:!MD5;`n`n"

    if ($onionHost) {
        $nginxSslConf += "    add_header Onion-Location http://$onionHost`$request_uri;`n`n"
    }

    $nginxSslConf += "    location / {`n"
    $nginxSslConf += "        proxy_pass         http://172.17.0.1:$APP_PORT;`n"
    $nginxSslConf += "        proxy_http_version 1.1;`n"
    $nginxSslConf += "        proxy_set_header   Upgrade `$http_upgrade;`n"
    $nginxSslConf += "        proxy_set_header   Connection keep-alive;`n"
    $nginxSslConf += "        proxy_set_header   Host `$host;`n"
    $nginxSslConf += "        proxy_set_header   X-Real-IP `$remote_addr;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-For `$proxy_add_x_forwarded_for;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-Proto `$scheme;`n"
    $nginxSslConf += "        proxy_cache_bypass `$http_upgrade;`n"
    $nginxSslConf += "    }`n"
    $nginxSslConf += "}`n"

    $nginxConfFile = Join-Path $env:TEMP "$APP_NAME.nginx.conf"
    Save-UnixFile $nginxConfFile $nginxSslConf
    SCP $nginxConfFile "/tmp/$APP_NAME.conf"
    SSH "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    SSH "docker exec nginx nginx -s reload"
    Write-Ok "Nginx configured for $DOMAIN (HTTPS)"
}

# -- Step 6: SSL (first time only) -------------------------------------------
if ($SSL) {
    Write-Step "Getting SSL certificate"

    SSH "apt-get install -y certbot"

$sslScript = @'
#!/bin/bash
set -e
echo 'Stopping docker nginx...'
docker stop nginx || true
sleep 2
echo 'Getting cert...'
certbot certonly --standalone -d __DOMAIN__ --non-interactive --agree-tos -m admin@__DOMAIN__
echo 'Starting docker nginx...'
docker start nginx
sleep 2
echo 'Done'
'@
$sslScript = $sslScript -replace '__DOMAIN__', $DOMAIN

    $sslScriptFile = Join-Path $env:TEMP "ssl-setup.sh"
    Save-UnixFile $sslScriptFile $sslScript
    Run-RemoteScript $sslScriptFile "/tmp/ssl-setup.sh"

    Write-Ok "Certificate obtained - re-applying nginx SSL config..."

    SSH "cp -L /etc/letsencrypt/live/$DOMAIN/fullchain.pem /tmp/fullchain.pem && cp -L /etc/letsencrypt/live/$DOMAIN/privkey.pem /tmp/privkey.pem"
    SSH "docker exec nginx mkdir -p /etc/letsencrypt/live/$DOMAIN"
    SSH "docker cp /tmp/fullchain.pem nginx:/etc/letsencrypt/live/$DOMAIN/fullchain.pem"
    SSH "docker cp /tmp/privkey.pem  nginx:/etc/letsencrypt/live/$DOMAIN/privkey.pem"

    $nginxSslConf  = "server {`n"
    $nginxSslConf += "    listen 80;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    return 301 https://`$host`$request_uri;`n"
    $nginxSslConf += "}`n`n"
    $nginxSslConf += "server {`n"
    $nginxSslConf += "    listen 443 ssl;`n"
    $nginxSslConf += "    server_name $DOMAIN www.$DOMAIN;`n"
    $nginxSslConf += "    ssl_certificate /etc/letsencrypt/live/$DOMAIN/fullchain.pem;`n"
    $nginxSslConf += "    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;`n"
    $nginxSslConf += "    ssl_protocols TLSv1.2 TLSv1.3;`n"
    $nginxSslConf += "    ssl_ciphers HIGH:!aNULL:!MD5;`n`n"
    $nginxSslConf += "    location / {`n"
    $nginxSslConf += "        proxy_pass         http://172.17.0.1:$APP_PORT;`n"
    $nginxSslConf += "        proxy_http_version 1.1;`n"
    $nginxSslConf += "        proxy_set_header   Upgrade `$http_upgrade;`n"
    $nginxSslConf += "        proxy_set_header   Connection keep-alive;`n"
    $nginxSslConf += "        proxy_set_header   Host `$host;`n"
    $nginxSslConf += "        proxy_set_header   X-Real-IP `$remote_addr;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-For `$proxy_add_x_forwarded_for;`n"
    $nginxSslConf += "        proxy_set_header   X-Forwarded-Proto `$scheme;`n"
    $nginxSslConf += "        proxy_cache_bypass `$http_upgrade;`n"
    $nginxSslConf += "    }`n"
    $nginxSslConf += "}`n"

    $nginxSslFile = Join-Path $env:TEMP "$APP_NAME.ssl.nginx.conf"
    Save-UnixFile $nginxSslFile $nginxSslConf
    SCP $nginxSslFile "/tmp/$APP_NAME.conf"
    SSH "docker cp /tmp/$APP_NAME.conf nginx:/etc/nginx/conf.d/$APP_NAME.conf"
    SSH "docker exec nginx nginx -s reload"

    Write-Ok "SSL installed for $DOMAIN"
}

# -- Step 7: Tor hidden service (first time only) ----------------------------
if ($Tor) {
    Write-Step "Setting up Tor hidden service for $DOMAIN"

$torScript = @'
#!/bin/bash
set -e
apt-get install -y tor
systemctl enable tor
if ! grep -q '__APP_NAME__' /etc/tor/torrc; then
  echo '' >> /etc/tor/torrc
  echo '# __APP_NAME__ hidden service' >> /etc/tor/torrc
  echo 'HiddenServiceDir /var/lib/tor/__APP_NAME__/' >> /etc/tor/torrc
  echo 'HiddenServicePort 80 127.0.0.1:__APP_PORT__' >> /etc/tor/torrc
fi
systemctl restart tor
sleep 5
echo 'Onion address:'
cat /var/lib/tor/__APP_NAME__/hostname
'@
$torScript = $torScript `
    -replace '__APP_NAME__', $APP_NAME `
    -replace '__APP_PORT__', $APP_PORT

    $torScriptFile = Join-Path $env:TEMP "tor-setup.sh"
    Save-UnixFile $torScriptFile $torScript
    Run-RemoteScript $torScriptFile "/tmp/tor-setup.sh"

    $onionAddress = SSH-Query "cat /var/lib/tor/$APP_NAME/hostname 2>/dev/null || echo 'not ready yet'"

    Write-Ok "Tor hidden service configured"
    Write-Host "    Onion: $onionAddress" -ForegroundColor Magenta
}

# -- Post-deploy smoke test (like the psake DeployWebApp retry loop) ---------
Write-Step "Smoke testing $REQUEST_PROTOCOL$DOMAIN"
$maxAttempts = 10
$attempt = 0
$ok = $false
while ($attempt -lt $maxAttempts) {
    $attempt++
    try {
        $resp = Invoke-WebRequest -Uri "$REQUEST_PROTOCOL$DOMAIN" -UseBasicParsing -TimeoutSec 15
        if ($resp.StatusCode -eq 200) { $ok = $true; break }
        Write-Warn "HTTP $($resp.StatusCode), retrying..."
    } catch {
        Write-Warn "Request $attempt/$maxAttempts failed: $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 5
}
if ($ok) { Write-Ok "Site returned 200" } else { Write-Warn "Site did not return 200 from the outside (DNS/firewall?)" }

# -- Done ---------------------------------------------------------------------
Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " Deployment complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host " Site:   $REQUEST_PROTOCOL$DOMAIN" -ForegroundColor White
if ($Tor) {
    $onion = SSH-Query "cat /var/lib/tor/$APP_NAME/hostname 2>/dev/null || echo 'pending'"
    Write-Host " Onion:  http://$onion" -ForegroundColor Magenta
}
Write-Host ""
Write-Host " Useful commands:" -ForegroundColor Gray
Write-Host "   Status: plink -ssh -pw PASSWORD $SSH_USER@$SSH_HOST systemctl status $APP_NAME" -ForegroundColor Gray
Write-Host "   Logs:   plink -ssh -pw PASSWORD $SSH_USER@$SSH_HOST journalctl -u $APP_NAME -f" -ForegroundColor Gray
Write-Host ""