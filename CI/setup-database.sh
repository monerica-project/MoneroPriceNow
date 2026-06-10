#!/usr/bin/env bash
# setup-database.sh — one-time (idempotent) PostgreSQL setup on the VPS for
# the price quote store. Safe to re-run: existing role/db/password are kept.
#
#   ./setup-database.sh
#
# What it does on the VPS:
#   1. Installs postgresql if missing (listens on 127.0.0.1 only by default).
#   2. Creates role + database (DB_USER / DB_NAME below) if missing, with a
#      random password generated once and persisted.
#   3. Writes /etc/$APP_NAME/db.env containing ConnectionStrings__PriceDb,
#      which the systemd unit (installed by deploy.sh) loads via
#      EnvironmentFile. The connection string never lives in the repo or in
#      rsynced files.
#
# Schema creation is NOT done here — the app applies EF migrations on startup.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/deploy-config.sh"   # provides VPS, APP_NAME

DB_NAME="${DB_NAME:-cryptopricenow}"
DB_USER="${DB_USER:-cryptopricenow}"
ENV_DIR="/etc/$APP_NAME"
ENV_FILE="$ENV_DIR/db.env"

C_CYAN=$'\033[36m'; C_GREEN=$'\033[32m'; C_RESET=$'\033[0m'
step() { echo; echo "${C_CYAN}==> $*${C_RESET}"; }
ok()   { echo "${C_GREEN}    OK: $*${C_RESET}"; }

# ---- 1. PostgreSQL installed + running --------------------------------------
step "Ensuring PostgreSQL is installed on $VPS"
if ! ssh "$VPS" 'command -v psql >/dev/null 2>&1'; then
    ssh "$VPS" "sudo apt-get update -qq && sudo apt-get install -y postgresql"
    ok "PostgreSQL installed"
else
    ok "PostgreSQL already installed"
fi
ssh "$VPS" "sudo systemctl enable --now postgresql"
ok "PostgreSQL running"

# ---- 2. Password: generate once, reuse forever ------------------------------
step "Resolving database password"
DB_PASS=""
if ssh "$VPS" "sudo test -f $ENV_FILE"; then
    DB_PASS="$(ssh "$VPS" "sudo grep -oP '(?<=Password=)[^;\"]+' $ENV_FILE" | head -1 | tr -d '[:space:]')"
fi
if [[ -z "$DB_PASS" ]]; then
    DB_PASS="$(openssl rand -hex 24)"
    ok "Generated new password"
else
    ok "Reusing existing password from $ENV_FILE"
fi

# ---- 3. Role + database (idempotent) -----------------------------------------
step "Ensuring role '$DB_USER' and database '$DB_NAME'"
ssh "$VPS" "sudo -u postgres psql -v ON_ERROR_STOP=1 <<'SQL'
SELECT 'exists' FROM pg_roles WHERE rolname = '$DB_USER';
SQL" >/dev/null 2>&1 || true

ssh "$VPS" "sudo -u postgres psql -v ON_ERROR_STOP=1 -tAc \
  \"SELECT 1 FROM pg_roles WHERE rolname='$DB_USER'\"" | grep -q 1 \
  || ssh "$VPS" "sudo -u postgres psql -v ON_ERROR_STOP=1 -c \
       \"CREATE ROLE $DB_USER LOGIN PASSWORD '$DB_PASS'\""

# Keep the role password in sync with the env file (covers regenerated files)
ssh "$VPS" "sudo -u postgres psql -v ON_ERROR_STOP=1 -c \
  \"ALTER ROLE $DB_USER WITH LOGIN PASSWORD '$DB_PASS'\""

ssh "$VPS" "sudo -u postgres psql -v ON_ERROR_STOP=1 -tAc \
  \"SELECT 1 FROM pg_database WHERE datname='$DB_NAME'\"" | grep -q 1 \
  || ssh "$VPS" "sudo -u postgres createdb -O $DB_USER $DB_NAME"

# EF migrations run as DB_USER and need full rights on the public schema
ssh "$VPS" "sudo -u postgres psql -v ON_ERROR_STOP=1 -d $DB_NAME -c \
  \"GRANT ALL ON SCHEMA public TO $DB_USER\""
ok "Role and database ready"

# ---- 4. Env file consumed by systemd ----------------------------------------
step "Writing $ENV_FILE"
CONN="Host=127.0.0.1;Port=5432;Database=$DB_NAME;Username=$DB_USER;Password=$DB_PASS"
ssh "$VPS" "sudo mkdir -p $ENV_DIR && \
  printf 'ConnectionStrings__PriceDb=%s\n' '$CONN' | sudo tee $ENV_FILE >/dev/null && \
  sudo chmod 600 $ENV_FILE && sudo chown root:root $ENV_FILE"
ok "Connection string installed (root-only, loaded by systemd EnvironmentFile)"

echo
echo "${C_GREEN}Database setup complete.${C_RESET}"
echo "  DB:      $DB_NAME"
echo "  User:    $DB_USER"
echo "  Env:     $ENV_FILE"
echo
echo "Next: run ./deploy.sh — the updated systemd unit loads $ENV_FILE and the"
echo "app applies EF migrations automatically on startup."
