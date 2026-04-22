# deploy-teardown.ps1
# Run from the CI folder:  .\deploy-teardown.ps1
#
# Completely removes everything deploy.ps1 installed on the server:
#   - Stops and disables the systemd service
#   - Deletes /var/www/moneropricenow
#   - Removes the nginx server block from the docker nginx container
#   - Deletes the Let's Encrypt cert (optional — pass -KeepCert to keep it)
#   - Removes the Tor hidden service entry + directory (optional — pass -KeepTor to keep it)
#   - Closes the app port in ufw

param(
    [switch]$KeepCert,
    [switch]$KeepTor,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$configPath = Join-Path $PSScriptRoot "deploy-config.ps1"
if (-not (Test-Path $configPath)) {
    Write-Error "deploy-config.ps1 not found next to deploy-teardown.ps1"
    exit 1
}
. $configPath

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    WARN: $msg" -ForegroundColor Yellow }

function SSH($cmd) {
    & $PLINK -ssh -pw $SSH_PASSWORD -batch "$SSH_USER@$SSH_HOST" $cmd 2>&1 | Out-Host
    # don't fail the whole script on non-zero
}

function SSH-Ignore($cmd) {
    try {
        & $PLINK -ssh -pw $SSH_PASSWORD -batch "$SSH_USER@$SSH_HOST" $cmd 2>&1 | Out-Null
    } catch {
        # swallow — teardown is best-effort
    }
}

if (-not $Force) {
    Write-Host ""
    Write-Host "This will DELETE the $APP_NAME deployment from $SSH_HOST" -ForegroundColor Yellow
    Write-Host "  - systemd service: $APP_NAME" -ForegroundColor Yellow
    Write-Host "  - directory:       $DEPLOY_PATH" -ForegroundColor Yellow
    Write-Host "  - nginx config:    /etc/nginx/conf.d/$APP_NAME.conf (inside docker nginx)" -ForegroundColor Yellow
    if (-not $KeepCert) {
        Write-Host "  - Let's Encrypt cert for $DOMAIN" -ForegroundColor Yellow
    } else {
        Write-Host "  - (keeping Let's Encrypt cert)" -ForegroundColor Gray
    }
    if (-not $KeepTor) {
        Write-Host "  - Tor hidden service dir: /var/lib/tor/$APP_NAME" -ForegroundColor Yellow
    } else {
        Write-Host "  - (keeping Tor hidden service)" -ForegroundColor Gray
    }
    Write-Host ""
    $confirm = Read-Host "Type 'yes' to proceed"
    if ($confirm -ne 'yes') {
        Write-Warn "Aborted."
        exit 0
    }
}

# ── Stop and disable the systemd service ─────────────────────────────────────
Write-Step "Stopping and disabling $APP_NAME service"
SSH-Ignore "systemctl stop $APP_NAME"
SSH-Ignore "systemctl disable $APP_NAME"
SSH-Ignore "systemctl reset-failed $APP_NAME"
SSH-Ignore "rm -f /etc/systemd/system/$APP_NAME.service"
SSH-Ignore "systemctl daemon-reload"
Write-Ok "Service removed"

# ── Delete deployed app files ────────────────────────────────────────────────
Write-Step "Deleting $DEPLOY_PATH"
SSH-Ignore "rm -rf $DEPLOY_PATH"
Write-Ok "Deployment directory removed"

# ── Remove nginx config from docker nginx ────────────────────────────────────
Write-Step "Removing nginx config"
SSH-Ignore "docker exec nginx rm -f /etc/nginx/conf.d/$APP_NAME.conf"
SSH-Ignore "docker exec nginx rm -f /var/www/maintenance.html"
SSH-Ignore "docker exec nginx nginx -s reload"
Write-Ok "Nginx config removed and reloaded"

# ── Remove Let's Encrypt cert (unless -KeepCert) ─────────────────────────────
if (-not $KeepCert) {
    Write-Step "Deleting Let's Encrypt certificate for $DOMAIN"
    SSH-Ignore "certbot delete --cert-name $DOMAIN --non-interactive"
    Write-Ok "Cert removed"
} else {
    Write-Warn "Skipping cert deletion (-KeepCert)"
}

# ── Remove Tor hidden service entry ──────────────────────────────────────────
if (-not $KeepTor) {
    Write-Step "Removing Tor hidden service"
    # Strip out the block we added in deploy.ps1:
    #   # $APP_NAME hidden service
    #   HiddenServiceDir /var/lib/tor/$APP_NAME/
    #   HiddenServicePort 80 127.0.0.1:$APP_PORT
    SSH-Ignore "sed -i '/# $APP_NAME hidden service/,+2d' /etc/tor/torrc"
    SSH-Ignore "rm -rf /var/lib/tor/$APP_NAME"
    SSH-Ignore "systemctl restart tor"
    Write-Ok "Tor hidden service removed"
} else {
    Write-Warn "Skipping Tor removal (-KeepTor)"
}

# ── Close firewall port ──────────────────────────────────────────────────────
Write-Step "Closing firewall port $APP_PORT"
SSH-Ignore "ufw delete allow $APP_PORT/tcp"
Write-Ok "Port closed"

# ── Cleanup scratch files ────────────────────────────────────────────────────
Write-Step "Cleaning /tmp scratch files"
SSH-Ignore "rm -f /tmp/$APP_NAME.conf /tmp/maintenance.html /tmp/fullchain.pem /tmp/privkey.pem /tmp/ssl-setup.sh /tmp/tor-setup.sh /tmp/web.tar.gz"
Write-Ok "Done"

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " Teardown complete." -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host " Server $SSH_HOST no longer runs $APP_NAME." -ForegroundColor White
Write-Host ""