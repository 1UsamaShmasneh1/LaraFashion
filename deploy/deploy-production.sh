#!/usr/bin/env bash
set -Eeuo pipefail

DEPLOY_CONFIG="${LARAFASHION_DEPLOY_CONFIG:-/etc/larafashion-deploy.env}"
if [[ -f "$DEPLOY_CONFIG" ]]; then
    # Root-managed, non-secret deployment settings such as the localhost health URL.
    source "$DEPLOY_CONFIG"
fi

SERVICE_NAME="${LARAFASHION_SERVICE_NAME:-larafashion}"
REPOSITORY="${LARAFASHION_REPOSITORY:-/var/www/larafashion/LaraFashion}"
PROJECT_DIR="$REPOSITORY/LaraFashion"
PROJECT_FILE="$PROJECT_DIR/LaraFashion.csproj"
PERSISTENT_ROOT="${LARAFASHION_PERSISTENT_ROOT:-/var/www/larafashion}"
LIVE_DIR="${LARAFASHION_LIVE_DIR:-$PERSISTENT_ROOT/publish}"
RELEASES_DIR="${LARAFASHION_RELEASES_DIR:-$PERSISTENT_ROOT/releases}"
DATABASE_PATH="${LARAFASHION_DATABASE_PATH:-$PERSISTENT_ROOT/data/larafashion.db}"
UPLOADS_PATH="${LARAFASHION_UPLOADS_PATH:-$PERSISTENT_ROOT/uploads}"
BACKUP_DIR="${LARAFASHION_BACKUP_DIR:-$PERSISTENT_ROOT/backups}"
HEALTH_URL="${LARAFASHION_HEALTH_URL:-http://127.0.0.1:5000/health}"
DEPLOY_COMMIT="${1:-}"
TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"
STAGING_DIR="$RELEASES_DIR/.staging-${DEPLOY_COMMIT:0:12}-$TIMESTAMP"
ROLLBACK_DIR="$RELEASES_DIR/rollback-$TIMESTAMP"
BACKUP_PATH="$BACKUP_DIR/larafashion-$TIMESTAMP.db"
DB_CONNECTION="Data Source=$DATABASE_PATH;Mode=ReadWrite"
export LARAFASHION_EF_CONNECTION="$DB_CONNECTION"

SERVICE_STOPPED=0
FILES_SWAPPED=0
BACKUP_CREATED=0
DEPLOY_SUCCEEDED=0

log() { printf '[larafashion-deploy] %s\n' "$*"; }
fail() { log "ERROR: $*" >&2; exit 1; }

if [[ "${LARAFASHION_DEPLOY_LOCK_HELD:-0}" != "1" ]]; then
    exec 9>/tmp/larafashion-production-deploy.lock
    flock -w 900 9 || fail "Another production deployment is still running."
fi

rollback() {
    local exit_code=$?
    trap - ERR INT TERM
    if [[ "$DEPLOY_SUCCEEDED" == "1" ]]; then return 0; fi

    log "Deployment failed; starting rollback."
    if [[ "$SERVICE_STOPPED" == "0" && "$FILES_SWAPPED" == "0" && "$BACKUP_CREATED" == "0" ]]; then
        [[ -d "$STAGING_DIR" ]] && rm -rf "$STAGING_DIR"
        log "Failure occurred before production was stopped; no live state was changed."
        exit "$exit_code"
    fi

    sudo systemctl stop "$SERVICE_NAME" || true

    if [[ "$BACKUP_CREATED" == "1" && -s "$BACKUP_PATH" ]]; then
        rm -f "${DATABASE_PATH}-wal" "${DATABASE_PATH}-shm"
        cp "$BACKUP_PATH" "$DATABASE_PATH"
        log "Database restored from $BACKUP_PATH."
    fi

    if [[ "$FILES_SWAPPED" == "1" && -d "$ROLLBACK_DIR" ]]; then
        if [[ -d "$LIVE_DIR" ]]; then mv "$LIVE_DIR" "$RELEASES_DIR/failed-$TIMESTAMP"; fi
        mv "$ROLLBACK_DIR" "$LIVE_DIR"
        log "Previous application files restored."
    fi

    sudo systemctl start "$SERVICE_NAME" || true
    exit "$exit_code"
}
trap rollback ERR INT TERM

[[ "$DEPLOY_COMMIT" =~ ^[0-9a-fA-F]{40}$ ]] || fail "A full 40-character commit SHA is required."
for required_command in dotnet git flock curl sudo; do
    command -v "$required_command" >/dev/null 2>&1 || fail "Required command is not installed: $required_command"
done
[[ -f "$PROJECT_FILE" ]] || fail "Project file not found: $PROJECT_FILE"
[[ -f "$REPOSITORY/deploy/deploy-production.sh" ]] || fail "Version-controlled deployment script is missing."
[[ -f "$DATABASE_PATH" && -s "$DATABASE_PATH" ]] || fail "Production database is missing or empty: $DATABASE_PATH"
[[ -w "$DATABASE_PATH" ]] || fail "Deployment user cannot write the production database: $DATABASE_PATH"
[[ -d "$UPLOADS_PATH" ]] || fail "Persistent uploads directory is missing: $UPLOADS_PATH"

SERVICE_WORKING_DIR="$(sudo systemctl show "$SERVICE_NAME" --property=WorkingDirectory --value)"
SERVICE_EXEC_START="$(sudo systemctl show "$SERVICE_NAME" --property=ExecStart --value)"
SERVICE_USER="$(sudo systemctl show "$SERVICE_NAME" --property=User --value)"
SERVICE_USER="${SERVICE_USER:-root}"
[[ "$SERVICE_WORKING_DIR" == "$LIVE_DIR" ]] || fail "systemd WorkingDirectory is '$SERVICE_WORKING_DIR'; expected '$LIVE_DIR'."
[[ "$SERVICE_EXEC_START" == *"$LIVE_DIR/LaraFashion"* || "$SERVICE_EXEC_START" == *"$LIVE_DIR/LaraFashion.dll"* ]] || fail "systemd ExecStart does not run the application from $LIVE_DIR."
sudo -u "$SERVICE_USER" test -r "$DATABASE_PATH" || fail "Service user '$SERVICE_USER' cannot read the production database."
sudo -u "$SERVICE_USER" test -w "$DATABASE_PATH" || fail "Service user '$SERVICE_USER' cannot write the production database."
sudo -u "$SERVICE_USER" test -r "$UPLOADS_PATH" || fail "Service user '$SERVICE_USER' cannot read uploads."
sudo -u "$SERVICE_USER" test -w "$UPLOADS_PATH" || fail "Service user '$SERVICE_USER' cannot write uploads."

mkdir -p "$RELEASES_DIR" "$BACKUP_DIR"
rm -rf "$STAGING_DIR"

cd "$REPOSITORY"
[[ "$(git rev-parse HEAD)" == "$DEPLOY_COMMIT" ]] || fail "Repository HEAD does not match the workflow commit."

log "Restoring local tools and project dependencies."
dotnet tool restore
dotnet restore "$PROJECT_FILE"
dotnet build "$PROJECT_FILE" -c Release --no-restore

log "Checking migrations and model consistency."
dotnet tool run dotnet-ef migrations list --project "$PROJECT_FILE" --startup-project "$PROJECT_FILE" --configuration Release --no-connect --no-build
dotnet tool run dotnet-ef migrations has-pending-model-changes --project "$PROJECT_FILE" --startup-project "$PROJECT_FILE" --configuration Release --no-build

log "Publishing commit $DEPLOY_COMMIT to a temporary directory."
dotnet publish "$PROJECT_FILE" -c Release --no-restore --no-build -o "$STAGING_DIR"
[[ -x "$STAGING_DIR/LaraFashion" || -f "$STAGING_DIR/LaraFashion.dll" ]] || fail "Publish output does not contain LaraFashion."
if find "$STAGING_DIR" -type f \( -name '*.db' -o -name '*.db-wal' -o -name '*.db-shm' \) -print -quit | grep -q .; then
    fail "Publish output unexpectedly contains a SQLite database."
fi

log "Stopping $SERVICE_NAME before SQLite backup and migration."
sudo systemctl stop "$SERVICE_NAME"
SERVICE_STOPPED=1
sudo systemctl is-active --quiet "$SERVICE_NAME" && fail "Service did not stop."

if command -v sqlite3 >/dev/null 2>&1; then
    sqlite3 "$DATABASE_PATH" ".timeout 10000" ".backup '$BACKUP_PATH'"
else
    cp --preserve=mode,timestamps "$DATABASE_PATH" "$BACKUP_PATH"
fi
[[ -s "$BACKUP_PATH" ]] || fail "SQLite backup is missing or empty."
BACKUP_CREATED=1
log "Database backup created: $BACKUP_PATH"

log "Applying EF Core migrations to the existing production database."
cd "$REPOSITORY"
dotnet tool run dotnet-ef database update --project "$PROJECT_FILE" --startup-project "$PROJECT_FILE" --configuration Release --connection "$DB_CONNECTION" --no-build

if [[ -d "$LIVE_DIR" ]]; then
    mv "$LIVE_DIR" "$ROLLBACK_DIR"
    FILES_SWAPPED=1
fi
mv "$STAGING_DIR" "$LIVE_DIR"
FILES_SWAPPED=1

log "Starting $SERVICE_NAME."
sudo systemctl start "$SERVICE_NAME"
SERVICE_STOPPED=0

HEALTHY=0
for attempt in {1..12}; do
    if sudo systemctl is-active --quiet "$SERVICE_NAME" && curl --fail --silent --show-error --location --insecure --max-time 5 "$HEALTH_URL" >/dev/null; then
        HEALTHY=1
        break
    fi
    sleep 5
done
[[ "$HEALTHY" == "1" ]] || fail "Health check failed: $HEALTH_URL"

DEPLOY_SUCCEEDED=1
trap - ERR INT TERM
log "Deployment and health check completed successfully."

# Keep the current backup regardless of retention, and prune older successful backups.
find "$BACKUP_DIR" -maxdepth 1 -type f -name 'larafashion-*.db' ! -path "$BACKUP_PATH" -mtime +14 -delete
find "$BACKUP_DIR" -maxdepth 1 -type f -name 'larafashion-*.db' ! -path "$BACKUP_PATH" -printf '%T@ %p\n' \
    | sort -nr | awk 'NR>10 { sub(/^[^ ]+ /, ""); print }' | xargs -r rm -f --

if [[ -d "$ROLLBACK_DIR" ]]; then
    log "Previous release retained for manual inspection at $ROLLBACK_DIR."
fi
