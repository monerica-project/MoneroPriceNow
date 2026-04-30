#!/usr/bin/env bash
# deploy-windows.sh
# Deploy CryptoPriceNow.Web to a Windows IIS server from a Linux dev box.
#
# Run from the CI folder:  ./deploy-windows.sh
#
# Flags:
#   --skip-build   skip dotnet publish
#   --restart-only stop+start the app pool, no file changes (smoke test)
#
# Reads deploy-config-windows.sh next to this script.

set -euo pipefail

# -- Parse flags --------------------------------------------------------------
SKIP_BUILD=0
RESTART_ONLY=0
for arg in "$@"; do
    case "$arg" in
        --skip-build)   SKIP_BUILD=1 ;;
        --restart-only) RESTART_ONLY=1 ;;
        -h|--help)
            sed -n '2,12p' "$0"
            exit 0
            ;;
        *) echo "Unknown flag: $arg" >&2; exit 1 ;;
    esac
done

# -- Locate self / load config ------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_PATH="$SCRIPT_DIR/deploy-config-windows.sh"
if [[ ! -f "$CONFIG_PATH" ]]; then
    echo "ERROR: deploy-config-windows.sh not found next to deploy-windows.sh" >&2
    exit 1
fi
# shellcheck source=/dev/null
. "$CONFIG_PATH"

: "${SSH_HOST:?SSH_HOST not set}"
: "${SSH_USER:?SSH_USER not set}"
: "${IIS_SITE:?IIS_SITE not set}"
: "${IIS_APP_POOL:?IIS_APP_POOL not set}"
: "${IIS_PHYSICAL_PATH:?IIS_PHYSICAL_PATH not set}"
: "${WEB_PROJECT:?WEB_PROJECT not set}"
: "${DOMAIN:?DOMAIN not set}"
: "${REQUEST_PROTOCOL:=https://}"

# Resolve WEB_PROJECT relative to script if not absolute
if [[ "$WEB_PROJECT" != /* ]]; then
    WEB_PROJECT="$SCRIPT_DIR/$WEB_PROJECT"
fi

# -- Colors / helpers ---------------------------------------------------------
C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_YELLOW=$'\033[33m'
C_RED=$'\033[31m';  C_GRAY=$'\033[90m'; C_RESET=$'\033[0m'

step() { echo; echo "${C_CYAN}==> $*${C_RESET}"; }
ok()   { echo "${C_GREEN}    OK: $*${C_RESET}"; }
warn() { echo "${C_YELLOW}    WARN: $*${C_RESET}"; }
errx() { echo "${C_RED}ERROR: $*${C_RESET}" >&2; exit 1; }

TMPDIR_LOCAL="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_LOCAL"' EXIT

# -- SSH wrappers (password auth via sshpass) --------------------------------
# BatchMode is OFF so password auth can be attempted; sshpass injects it.
SSH_OPTS=(-o StrictHostKeyChecking=accept-new -o ServerAliveInterval=30 -o PubkeyAuthentication=no -o PreferredAuthentications=password)

if [[ -z "${SSH_PASSWORD:-}" ]]; then
    errx "SSH_PASSWORD is empty in deploy-config-windows.sh — required for password auth."
fi
if ! command -v sshpass >/dev/null 2>&1; then
    errx "sshpass not installed. Run: sudo apt install sshpass"
fi

# Run a CMD command on the Windows box
ssh_cmd() {
    sshpass -p "$SSH_PASSWORD" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" "$1"
}

# Run a PowerShell snippet on the Windows box.
# Encodes via base64 so we don't have to escape quotes/dollar signs through
# Linux shell -> ssh -> Windows cmd -> powershell.
ssh_ps() {
    local script="$1"
    local b64
    b64="$(printf '%s' "$script" | iconv -t UTF-16LE | base64 -w 0)"
    sshpass -p "$SSH_PASSWORD" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" "powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $b64"
}

# Same as ssh_ps but tolerates non-zero exit
ssh_ps_ignore() {
    local script="$1"
    local b64
    b64="$(printf '%s' "$script" | iconv -t UTF-16LE | base64 -w 0)"
    sshpass -p "$SSH_PASSWORD" ssh "${SSH_OPTS[@]}" "$SSH_USER@$SSH_HOST" "powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand $b64" || true
}

scp_send() {
    local local_path="$1" remote_path="$2"
    sshpass -p "$SSH_PASSWORD" scp -o StrictHostKeyChecking=accept-new -o PubkeyAuthentication=no -o PreferredAuthentications=password "$local_path" "$SSH_USER@$SSH_HOST:$remote_path"
}

# -- Helpers for app pool state ----------------------------------------------
stop_app_pool() {
    step "Stopping app pool: $IIS_APP_POOL"
    ssh_ps "
Import-Module WebAdministration
\$pool = Get-Item 'IIS:\AppPools\\$IIS_APP_POOL' -ErrorAction Stop
if (\$pool.State -eq 'Started') {
    Stop-WebAppPool -Name '$IIS_APP_POOL'
    \$tries = 0
    while ((Get-WebAppPoolState -Name '$IIS_APP_POOL').Value -ne 'Stopped' -and \$tries -lt 30) {
        Start-Sleep -Milliseconds 500
        \$tries++
    }
    Write-Host \"    Pool stopped after \$tries half-second checks\"
} else {
    Write-Host '    Pool was already stopped'
}
"
    ok "App pool stopped"
}

start_app_pool() {
    step "Starting app pool: $IIS_APP_POOL"
    ssh_ps "
Import-Module WebAdministration
Start-WebAppPool -Name '$IIS_APP_POOL'
\$tries = 0
while ((Get-WebAppPoolState -Name '$IIS_APP_POOL').Value -ne 'Started' -and \$tries -lt 30) {
    Start-Sleep -Milliseconds 500
    \$tries++
}
Write-Host \"    Pool started after \$tries half-second checks\"
"
    ok "App pool started"
}

# -- --restart-only short-circuit --------------------------------------------
if (( RESTART_ONLY )); then
    stop_app_pool
    start_app_pool
    step "Smoke testing $REQUEST_PROTOCOL$DOMAIN"
    code="$(curl -ksS -o /dev/null -w '%{http_code}' --max-time 15 "$REQUEST_PROTOCOL$DOMAIN" 2>/dev/null || echo 000)"
    [[ "$code" == "200" ]] && ok "Site returned 200" || warn "Got HTTP ${code:-000}"
    exit 0
fi

# -- Step 1: Build (linux dev box runs dotnet for win-x64) -------------------
WEB_OUT="$SCRIPT_DIR/../publish/web-windows"
WEB_OUT="$(realpath -m "$WEB_OUT")"

if (( ! SKIP_BUILD )); then
    step "Building web app (win-x64) on Linux"
    if ! command -v dotnet >/dev/null 2>&1; then
        errx "dotnet not found on local machine. Install the .NET SDK."
    fi
    rm -rf "$WEB_OUT"
    dotnet publish "$WEB_PROJECT" \
        -c Release \
        -r win-x64 \
        --self-contained false \
        -o "$WEB_OUT"
    ok "Web app built at $WEB_OUT"
fi

# Sanity: the published output must contain the dll
if [[ ! -f "$WEB_OUT/CryptoPriceNow.Web.dll" ]]; then
    errx "Expected $WEB_OUT/CryptoPriceNow.Web.dll not found. Did publish succeed?"
fi

# -- Step 2: Make sure the App_Offline mechanism gates the site --------------
# IIS recognises App_Offline.htm in the site root and serves it instead of
# the app while it exists. Drop one in BEFORE we touch files, so users see
# a friendly page instead of a half-deployed mess.
step "Enabling App_Offline page"

OFFLINE_LOCAL="$TMPDIR_LOCAL/App_Offline.htm"
cat > "$OFFLINE_LOCAL" <<'HTMLEOF'
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta http-equiv="refresh" content="15">
  <title>Updating - MoneroPriceNow</title>
  <style>
    body{min-height:100vh;display:flex;align-items:center;justify-content:center;
         background:#0f0f0f;color:#e0e0e0;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;margin:0}
    .card{text-align:center;padding:3rem 2.5rem;max-width:440px}
    h1{font-size:1.5rem;color:#f7931a;margin:0 0 .75rem}
    p{font-size:.95rem;line-height:1.6;color:#aaa;margin:0 0 .5rem}
    .note{margin-top:1.75rem;font-size:.8rem;color:#555}
  </style>
</head>
<body>
  <div class="card">
    <h1>Updating in progress</h1>
    <p>MoneroPriceNow is being updated and will be back shortly.</p>
    <p class="note">This page refreshes automatically every 15 seconds.</p>
  </div>
</body>
</html>
HTMLEOF

# Convert IIS_PHYSICAL_PATH into something scp-friendly via the admin share.
# We'll just have PowerShell copy it instead — cleaner.
OFFLINE_REMOTE_TMP='C:\Windows\Temp\App_Offline.htm'
scp_send "$OFFLINE_LOCAL" "/C:/Windows/Temp/App_Offline.htm"

ssh_ps "
\$dest = '$IIS_PHYSICAL_PATH\\App_Offline.htm'
Copy-Item -Path '$OFFLINE_REMOTE_TMP' -Destination \$dest -Force
Write-Host \"    App_Offline.htm placed at \$dest\"
"
ok "Site is in maintenance mode"

# Give IIS a moment to release file handles after App_Offline is seen.
sleep 3

# -- Step 3: Stop the app pool to release locks ------------------------------
stop_app_pool

# -- Step 4: Ship the build artifact -----------------------------------------
step "Packaging and uploading build"

WEB_ZIP="$TMPDIR_LOCAL/web.zip"
( cd "$WEB_OUT" && zip -qr "$WEB_ZIP" . )

REMOTE_ZIP='C:\Windows\Temp\moneropricenow-deploy.zip'
scp_send "$WEB_ZIP" "/C:/Windows/Temp/moneropricenow-deploy.zip"
ok "Uploaded $(du -h "$WEB_ZIP" | cut -f1) zip"

# -- Step 5: Extract on the server, preserving App_Offline.htm ---------------
step "Extracting onto $IIS_PHYSICAL_PATH"

ssh_ps "
\$site    = '$IIS_PHYSICAL_PATH'
\$zip     = '$REMOTE_ZIP'
\$offline = Join-Path \$site 'App_Offline.htm'

# Save App_Offline.htm so the wipe below can't take it down accidentally
\$tmpOffline = 'C:\\Windows\\Temp\\App_Offline.preserve.htm'
if (Test-Path \$offline) { Copy-Item \$offline \$tmpOffline -Force }

# Wipe everything except App_Offline.htm
Get-ChildItem -Path \$site -Force | Where-Object { \$_.Name -ne 'App_Offline.htm' } |
    Remove-Item -Recurse -Force -ErrorAction Stop

# Extract new build
Expand-Archive -Path \$zip -DestinationPath \$site -Force

# Restore App_Offline if it survived (it should — we filtered it out)
if (-not (Test-Path \$offline) -and (Test-Path \$tmpOffline)) {
    Copy-Item \$tmpOffline \$offline -Force
}

Remove-Item \$zip -Force -ErrorAction SilentlyContinue
Write-Host \"    Extraction complete\"
"
ok "Files deployed"

# -- Step 6: Start the app pool ----------------------------------------------
start_app_pool

# -- Step 7: Remove App_Offline.htm so the site goes live --------------------
step "Removing App_Offline.htm"
ssh_ps "
\$offline = '$IIS_PHYSICAL_PATH\\App_Offline.htm'
if (Test-Path \$offline) {
    Remove-Item \$offline -Force
    Write-Host '    App_Offline.htm removed'
} else {
    Write-Host '    App_Offline.htm already absent'
}
"
ok "Site is live"

# -- Step 8: Wait for IIS to warm up the app ---------------------------------
step "Waiting for app to respond on localhost"
ssh_ps_ignore "
\$max = 24; \$attempt = 0; \$status = ''
while (\$attempt -lt \$max) {
    \$attempt++
    try {
        \$r = Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost' -Headers @{ Host = '$DOMAIN' } -TimeoutSec 5 -ErrorAction Stop
        \$status = \$r.StatusCode
    } catch {
        \$status = 'err'
    }
    if (\$status -eq 200) {
        Write-Host \"    App responded HTTP 200 after \$attempt tries\"
        exit 0
    }
    Write-Host \"    Attempt \$attempt/\$max status=\$status, retrying in 5s...\"
    Start-Sleep -Seconds 5
}
Write-Host '    App did not return 200 locally — check Event Viewer / stdout logs in $IIS_PHYSICAL_PATH\\logs'
"

# -- Post-deploy smoke test from the dev box ---------------------------------
step "Smoke testing $REQUEST_PROTOCOL$DOMAIN from this machine"
SMOKE_MAX=10
SMOKE_OK=0
for ((i=1; i<=SMOKE_MAX; i++)); do
    code="$(curl -ksS -o /dev/null -w '%{http_code}' --max-time 15 "$REQUEST_PROTOCOL$DOMAIN" 2>/dev/null || echo 000)"
    code="${code:-000}"
    if [[ "$code" == "200" ]]; then
        SMOKE_OK=1
        break
    fi
    warn "Request $i/$SMOKE_MAX got HTTP $code, retrying..."
    sleep 5
done
if (( SMOKE_OK )); then
    ok "Site returned 200"
else
    warn "Site did not return 200 from the outside (DNS/cert/firewall?)"
fi

# -- Done ---------------------------------------------------------------------
echo
echo "${C_GREEN}============================================${C_RESET}"
echo "${C_GREEN} Deployment complete!${C_RESET}"
echo "${C_GREEN}============================================${C_RESET}"
echo " Site:   $REQUEST_PROTOCOL$DOMAIN"
echo
echo "${C_GRAY} Useful commands:${C_RESET}"
echo "${C_GRAY}   Restart only: ./deploy-windows.sh --restart-only${C_RESET}"
echo "${C_GRAY}   Skip build:   ./deploy-windows.sh --skip-build${C_RESET}"
echo "${C_GRAY}   Tail stdout logs: ssh $SSH_USER@$SSH_HOST 'powershell -Command \"Get-ChildItem $IIS_PHYSICAL_PATH\\logs\\stdout*.log | Sort LastWriteTime -Desc | Select -First 1 | Get-Content -Tail 60\"'${C_RESET}"
echo
