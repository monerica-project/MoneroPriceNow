#!/usr/bin/env bash
# deploy.sh — publish, rsync, install systemd unit + nginx conf, ensure TLS, restart.
# Run from the CI folder:  ./deploy.sh
#
# Flags:
#   --skip-build   skip dotnet publish
#   --no-ssl       skip TLS setup (useful when DNS isn't pointed yet)

set -euo pipefail

SKIP_BUILD=0
SKIP_SSL=0
for arg in "$@"; do
    case "$arg" in
        --skip-build) SKIP_BUILD=1 ;;
        --no-ssl)     SKIP_SSL=1 ;;
        --ssl)        : ;;  # accepted for backward compat (now default)
        -h|--help)    sed -n '2,9p' "$0"; exit 0 ;;
        *)            echo "Unknown flag: $arg" >&2; exit 1 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/deploy-config.sh"

[[ "$WEB_PROJECT" == /* ]] || WEB_PROJECT="$SCRIPT_DIR/$WEB_PROJECT"

TOR_KEYS_DIR="${TOR_KEYS_DIR:-}"
ONION_HOST=""

if [[ -n "$TOR_KEYS_DIR" ]]; then
    [[ -f "$TOR_KEYS_DIR/hostname" ]] || { echo "ERROR: $TOR_KEYS_DIR/hostname not found" >&2; exit 1; }
    [[ -f "$TOR_KEYS_DIR/hs_ed25519_public_key" ]] || { echo "ERROR: $TOR_KEYS_DIR/hs_ed25519_public_key not found" >&2; exit 1; }
    [[ -f "$TOR_KEYS_DIR/hs_ed25519_secret_key" ]] || { echo "ERROR: $TOR_KEYS_DIR/hs_ed25519_secret_key not found" >&2; exit 1; }
    ONION_HOST="$(tr -d '[:space:]' < "$TOR_KEYS_DIR/hostname")"
fi

C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_RED=$'\033[31m'; C_YELLOW=$'\033[33m'; C_RESET=$'\033[0m'
step() { echo; echo "${C_CYAN}==> $*${C_RESET}"; }
ok()   { echo "${C_GREEN}    OK: $*${C_RESET}"; }
warn() { echo "${C_YELLOW}    WARN: $*${C_RESET}"; }
errx() { echo "${C_RED}ERROR: $*${C_RESET}" >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---- Build -----------------------------------------------------------------
WEB_OUT="$SCRIPT_DIR/../publish/web"
WEB_OUT="$(realpath -m "$WEB_OUT")"

if (( ! SKIP_BUILD )); then
    step "Publishing $APP_NAME (linux-x64)"
    rm -rf "$WEB_OUT"
    # Force clean by deleting bin/obj from the project (and any referenced projects)
    # so Razor view recompilation always happens
    PROJECT_DIR="$(dirname "$WEB_PROJECT")"
    find "$PROJECT_DIR/.." -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
    dotnet publish "$WEB_PROJECT" \
        -c Release -r linux-x64 --self-contained false \
        -o "$WEB_OUT" --nologo -v minimal
    ok "Published to $WEB_OUT"
fi

# ---- Rsync binaries --------------------------------------------------------
step "Syncing binaries to $VPS:$DEPLOY_PATH"
ssh "$VPS" "sudo mkdir -p $DEPLOY_PATH"
rsync -rlptDz --delete --rsync-path="sudo rsync" \
    --exclude='appsettings.Production.json' \
    "$WEB_OUT/" "$VPS:$DEPLOY_PATH/"
ssh "$VPS" "sudo chown -R $SERVICE_USER:$SERVICE_USER $DEPLOY_PATH"
ok "Binaries synced"

# ---- Systemd unit ----------------------------------------------------------
step "Installing systemd unit"
SVC_FILE="$TMP/$APP_NAME.service"
cat > "$SVC_FILE" <<EOF
[Unit]
Description=$APP_NAME ($DOMAIN)
After=network.target

[Service]
WorkingDirectory=$DEPLOY_PATH
ExecStart=/usr/bin/dotnet $DEPLOY_PATH/CryptoPriceNow.Web.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=$APP_NAME
User=$SERVICE_USER
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:$APP_PORT
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ProtectHome=true
ReadWritePaths=$DEPLOY_PATH

[Install]
WantedBy=multi-user.target
EOF

scp "$SVC_FILE" "$VPS:/tmp/$APP_NAME.service"
ssh "$VPS" "sudo mv /tmp/$APP_NAME.service /etc/systemd/system/"
ssh "$VPS" "sudo systemctl daemon-reload"
ssh "$VPS" "sudo systemctl enable $APP_NAME"
ssh "$VPS" "sudo systemctl restart $APP_NAME"
ok "Service started"

# ---- Tor hidden service ----------------------------------------------------
if [[ -n "$TOR_KEYS_DIR" ]]; then
    step "Checking Tor hidden service"
    TOR_DIR="/var/lib/tor/$APP_NAME"

    # Is Tor already set up correctly with our keys? If so, skip the whole block.
    EXISTING_ONION=""
    if ssh "$VPS" "sudo test -f $TOR_DIR/hostname"; then
        EXISTING_ONION="$(ssh "$VPS" "sudo cat $TOR_DIR/hostname" | tr -d '[:space:]')"
    fi

    if [[ "$EXISTING_ONION" == "$ONION_HOST" ]] && ssh "$VPS" "systemctl is-active --quiet tor"; then
        ok "Tor already configured for $ONION_HOST — skipping setup"
    else
        echo "    First-time Tor setup or mismatch — installing..."

        if ! ssh "$VPS" 'command -v tor >/dev/null 2>&1'; then
            ssh "$VPS" "sudo apt-get install -y tor"
        fi

        ssh "$VPS" "sudo systemctl stop tor 2>/dev/null || true"
        ssh "$VPS" "sudo mkdir -p $TOR_DIR"
        ssh "$VPS" "sudo chown debian-tor:debian-tor $TOR_DIR"
        ssh "$VPS" "sudo chmod 700 $TOR_DIR"
        ssh "$VPS" "sudo rm -f $TOR_DIR/hostname $TOR_DIR/hs_ed25519_public_key $TOR_DIR/hs_ed25519_secret_key"

        echo "    Uploading keys..."
        scp "$TOR_KEYS_DIR/hostname" "$VPS:/tmp/_tor_hostname"
        scp "$TOR_KEYS_DIR/hs_ed25519_public_key" "$VPS:/tmp/_tor_pubkey"
        scp "$TOR_KEYS_DIR/hs_ed25519_secret_key" "$VPS:/tmp/_tor_seckey"

        ssh "$VPS" "sudo mv /tmp/_tor_hostname $TOR_DIR/hostname"
        ssh "$VPS" "sudo mv /tmp/_tor_pubkey $TOR_DIR/hs_ed25519_public_key"
        ssh "$VPS" "sudo mv /tmp/_tor_seckey $TOR_DIR/hs_ed25519_secret_key"

        ssh "$VPS" "sudo chown debian-tor:debian-tor $TOR_DIR/hostname $TOR_DIR/hs_ed25519_public_key $TOR_DIR/hs_ed25519_secret_key"
        ssh "$VPS" "sudo chmod 600 $TOR_DIR/hs_ed25519_secret_key"
        ssh "$VPS" "sudo chmod 644 $TOR_DIR/hs_ed25519_public_key $TOR_DIR/hostname"

        ssh "$VPS" "grep -q 'HiddenServiceDir $TOR_DIR' /etc/tor/torrc || (echo '' | sudo tee -a /etc/tor/torrc >/dev/null && echo 'HiddenServiceDir $TOR_DIR/' | sudo tee -a /etc/tor/torrc >/dev/null && echo 'HiddenServicePort 80 127.0.0.1:$APP_PORT' | sudo tee -a /etc/tor/torrc >/dev/null)"

        ssh "$VPS" "sudo systemctl enable tor"
        ssh "$VPS" "sudo systemctl restart tor"

        sleep 3
        REMOTE_ONION="$(ssh "$VPS" "sudo cat $TOR_DIR/hostname" | tr -d '[:space:]')"
        if [[ "$REMOTE_ONION" != "$ONION_HOST" ]]; then
            errx "Onion mismatch! Expected: $ONION_HOST  Got: $REMOTE_ONION"
        fi
        ok "Tor configured for $ONION_HOST"
    fi
fi

# ---- Nginx config ----------------------------------------------------------
step "Installing nginx config for $DOMAIN"
NGINX_FILE="$TMP/$APP_NAME.conf"

ONION_HEADER=""
if [[ -n "$ONION_HOST" ]]; then
    ONION_HEADER="        add_header Onion-Location http://$ONION_HOST\$request_uri;"
fi

# Decide whether to include www.* in server_name (apex domains only)
DOTS=$(echo "$DOMAIN" | tr -cd '.' | wc -c)
if [[ "$DOTS" == "1" ]]; then
    SERVER_NAME_LINE="    server_name $DOMAIN www.$DOMAIN;"
else
    SERVER_NAME_LINE="    server_name $DOMAIN;"
fi

cat > "$NGINX_FILE" <<EOF
server {
    listen 80;
    listen [::]:80;
$SERVER_NAME_LINE

    location /.well-known/acme-challenge/ {
        root /var/www/html;
    }

    client_max_body_size 64M;

    location / {
$ONION_HEADER
        proxy_pass         http://127.0.0.1:$APP_PORT;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade           \$http_upgrade;
        proxy_set_header   Connection        keep-alive;
        proxy_set_header   Host              \$host;
        proxy_cache_bypass \$http_upgrade;
        proxy_set_header   X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto \$scheme;
        proxy_set_header   X-Real-IP         \$remote_addr;
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;
    }

    server_tokens off;
}
EOF

scp "$NGINX_FILE" "$VPS:/tmp/$APP_NAME.conf"
ssh "$VPS" "sudo mv /tmp/$APP_NAME.conf /etc/nginx/sites-available/"
ssh "$VPS" "sudo ln -sf /etc/nginx/sites-available/$APP_NAME.conf /etc/nginx/sites-enabled/$APP_NAME.conf"
ssh "$VPS" "sudo nginx -t"
ssh "$VPS" "sudo systemctl reload nginx"
ok "Nginx configured"

# ---- TLS (always — idempotent) --------------------------------------------
HAS_TLS=0
if (( ! SKIP_SSL )); then
    step "Ensuring TLS cert for $DOMAIN"

    # Check if cert already exists
    if ssh "$VPS" "sudo test -f /etc/letsencrypt/live/$DOMAIN/fullchain.pem"; then
        ok "TLS cert already present for $DOMAIN — nginx config will use it"
        # Re-run certbot --nginx to make sure the existing cert is wired into the
        # nginx config we just rewrote. This is fast and won't request a new cert.
        if [[ "$DOTS" == "1" ]]; then
            ssh "$VPS" "sudo certbot --nginx --non-interactive --reinstall -d $DOMAIN -d www.$DOMAIN --redirect" || warn "certbot reinstall failed"
        else
            ssh "$VPS" "sudo certbot --nginx --non-interactive --reinstall -d $DOMAIN --redirect" || warn "certbot reinstall failed"
        fi
        HAS_TLS=1
    else
        # Try to issue a new cert. If DNS isn't pointed at this VPS yet, this fails;
        # warn but don't abort the deploy — site still works on HTTP.
        if [[ "$DOTS" == "1" ]]; then
            if ssh "$VPS" "sudo certbot --nginx --non-interactive --agree-tos -m admin@$DOMAIN -d $DOMAIN -d www.$DOMAIN --redirect"; then
                ok "TLS issued for $DOMAIN + www.$DOMAIN"
                HAS_TLS=1
            else
                warn "certbot failed — DNS may not be pointed at this VPS yet. Site is HTTP-only."
            fi
        else
            if ssh "$VPS" "sudo certbot --nginx --non-interactive --agree-tos -m admin@$DOMAIN -d $DOMAIN --redirect"; then
                ok "TLS issued for $DOMAIN"
                HAS_TLS=1
            else
                warn "certbot failed — DNS may not be pointed at this VPS yet. Site is HTTP-only."
            fi
        fi
    fi
fi

# ---- Smoke test ------------------------------------------------------------
PROTO="http://"
(( HAS_TLS )) && PROTO="https://"

step "Smoke testing $PROTO$DOMAIN"
SMOKE_OK=0
for i in 1 2 3 4 5 6 7 8 9 10; do
    code=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 15 "$PROTO$DOMAIN" || echo 000)
    if [[ "$code" == "200" || "$code" == "301" || "$code" == "302" ]]; then
        ok "Site returned HTTP $code"
        SMOKE_OK=1
        break
    fi
    echo "    Attempt $i/10 got HTTP $code, retrying in 5s..."
    sleep 5
done
(( SMOKE_OK )) || warn "Smoke test didn't return 200/301/302."

echo
echo "${C_GREEN}===============================${C_RESET}"
echo "${C_GREEN} Deploy complete${C_RESET}"
echo "${C_GREEN}===============================${C_RESET}"
echo " Site:    $PROTO$DOMAIN"
[[ -n "$ONION_HOST" ]] && echo " Onion:   http://$ONION_HOST"
echo " Status:  ssh $VPS sudo systemctl status $APP_NAME"
echo " Logs:    ssh $VPS journalctl -u $APP_NAME -f"
echo
