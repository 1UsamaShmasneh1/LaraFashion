#!/usr/bin/env bash
set +x
set -Eeuo pipefail
umask 077
log() { printf '[larafashion-deploy] %s\n' "$*"; }
fail() { log "ERROR: $*" >&2; return 1; }
DEPLOY_CONFIG="${LARAFASHION_DEPLOY_CONFIG:-/etc/larafashion-deploy.env}"
if [[ -f "$DEPLOY_CONFIG" ]]; then
    [[ ! -L "$DEPLOY_CONFIG" && "$(stat -c %u "$DEPLOY_CONFIG")" == 0 ]] || { log 'Untrusted config'; exit 1; }
    CONFIG_MODE="$(stat -c %a "$DEPLOY_CONFIG")"
    (( (8#$CONFIG_MODE & 0022) == 0 )) || { log 'Writable config is untrusted'; exit 1; }
    source "$DEPLOY_CONFIG"
fi
SERVICE_NAME="${LARAFASHION_SERVICE_NAME:-larafashion}"
REPOSITORY="${LARAFASHION_REPOSITORY:-/var/www/larafashion/LaraFashion}"
PERSISTENT_ROOT="${LARAFASHION_PERSISTENT_ROOT:-/var/www/larafashion}"
LIVE_DIR="${LARAFASHION_LIVE_DIR:-$PERSISTENT_ROOT/publish}"
RELEASES_DIR="${LARAFASHION_RELEASES_DIR:-$PERSISTENT_ROOT/releases}"
DATABASE_PATH="${LARAFASHION_DATABASE_PATH:-$PERSISTENT_ROOT/data/larafashion.db}"
UPLOADS_PATH="${LARAFASHION_UPLOADS_PATH:-$PERSISTENT_ROOT/uploads}"
BACKUP_DIR="${LARAFASHION_BACKUP_DIR:-$PERSISTENT_ROOT/backups}"
LOCK_FILE="${LARAFASHION_DEPLOY_LOCK_FILE:-/tmp/larafashion-production-deploy.lock}"
HEALTH_URL="${LARAFASHION_HEALTH_URL:-http://127.0.0.1:5000/health}"
KEYS_DIR="${LARAFASHION_DATA_PROTECTION_PATH:-}"
SERVICE_ENV_FILE="${LARAFASHION_SERVICE_ENV_FILE:-}"
FIRST_TRANSITION="${LARAFASHION_FIRST_TRANSITION:-0}"
TRANSITION_RUN_ID="${LARAFASHION_TRANSITION_RUN_ID:-}"
TRANSITION_ROOT="/var/www/larafashion/backups/first-transition/$TRANSITION_RUN_ID"
transition() { sudo --preserve-env=LARAFASHION_ACTION_RUN_ID python3 "$REPOSITORY/deploy/first-transition.py" "$1" "$TRANSITION_RUN_ID" "$DEPLOY_COMMIT" "${@:2}"; }
EVIDENCE_SOURCE="${LARAFASHION_PRE_IDENTITY_EVIDENCE_PATH:--}"
EVIDENCE_SHA="${LARAFASHION_PRE_IDENTITY_EVIDENCE_SHA256:--}"
WRITER_SERVICES="${LARAFASHION_WRITER_SERVICES:-$SERVICE_NAME}"
[[ " $WRITER_SERVICES " == *" $SERVICE_NAME "* ]] || { log 'Primary application must be a known writer'; exit 1; }
BUILD_TIMEOUT="${LARAFASHION_BUILD_TIMEOUT_SECONDS:-900}"
OPERATION_TIMEOUT="${LARAFASHION_MAINTENANCE_TIMEOUT_SECONDS:-600}"
SERVICE_TIMEOUT="${LARAFASHION_SERVICE_TIMEOUT_SECONDS:-60}"
DEPLOY_COMMIT="${1:-}"
[[ $# == 1 && "$DEPLOY_COMMIT" =~ ^[0-9a-f]{40}$ ]] || { log 'Full lowercase SHA required'; exit 1; }
[[ "$FIRST_TRANSITION" == 0 || "$FIRST_TRANSITION" == 1 ]] || { log 'Invalid first transition flag'; exit 1; }
if [[ "$FIRST_TRANSITION" == 1 ]]; then
    [[ "$TRANSITION_RUN_ID" =~ ^lf-cutover-[A-Za-z0-9-]+$ && "$PERSISTENT_ROOT" == /var/www/larafashion && "$SERVICE_NAME" == larafashion ]] || exit 1
    SERVICE_ENV_FILE="$TRANSITION_ROOT/runtime.env"
    KEYS_DIR=/home/ubuntu/.aspnet/DataProtection-Keys
fi
for command in dotnet git flock curl sudo realpath stat timeout df fuser find mv id; do
    command -v "$command" >/dev/null || { log "Missing command: $command"; exit 1; }
done
for value in "$BUILD_TIMEOUT" "$OPERATION_TIMEOUT" "$SERVICE_TIMEOUT"; do
    [[ "$value" =~ ^[1-9][0-9]*$ ]] || { log 'Invalid timeout'; exit 1; }
done
[[ ! -L "$LOCK_FILE" ]] || { log 'Symlink lock refused'; exit 1; }
for variable in REPOSITORY PERSISTENT_ROOT LIVE_DIR RELEASES_DIR DATABASE_PATH UPLOADS_PATH BACKUP_DIR LOCK_FILE; do
    [[ "${!variable}" == /* && "${!variable}" != / ]] || { log "Invalid path: $variable"; exit 1; }
    printf -v "$variable" '%s' "$(realpath -m -- "${!variable}")"
done
inside() { [[ "$1" == "$2/"* && "$1" != "$2" ]]; }
for path in "$LIVE_DIR" "$RELEASES_DIR" "$DATABASE_PATH" "$UPLOADS_PATH" "$BACKUP_DIR"; do
    inside "$path" "$PERSISTENT_ROOT" || { log 'Path outside persistent root'; exit 1; }
done
for path in "$DATABASE_PATH" "$BACKUP_DIR" "$UPLOADS_PATH"; do
    ! inside "$path" "$LIVE_DIR" && ! inside "$path" "$RELEASES_DIR" && ! inside "$path" "$REPOSITORY" || { log 'Evidence/data inside application/source'; exit 1; }
done
[[ "$BACKUP_DIR" != "$UPLOADS_PATH" && "$LIVE_DIR" != "$RELEASES_DIR" ]] || { log 'Overlapping paths'; exit 1; }
if [[ -n "${LARAFASHION_DEPLOY_HARNESS_ROOT:-}" ]]; then
    HARNESS_ROOT="$(realpath -e -- "$LARAFASHION_DEPLOY_HARNESS_ROOT")"
    [[ -f "$HARNESS_ROOT/.larafashion-deploy-harness" ]] || { log 'Harness marker missing'; exit 1; }
    for command in git sudo fuser curl dotnet; do
        [[ "$(command -v "$command")" == "$HARNESS_ROOT/mocks/"* ]] || { log 'Harness must mock all production-facing commands'; exit 1; }
    done
    for path in "$REPOSITORY" "$PERSISTENT_ROOT" "$LOCK_FILE"; do
        inside "$path" "$HARNESS_ROOT" || { log 'Harness escaped its root'; exit 1; }
    done
fi
[[ "$HEALTH_URL" =~ ^https?://(127\.0\.0\.1|localhost|\[::1\]):[0-9]+/health$ ]] || { log 'Explicit loopback health required'; exit 1; }
[[ -f "$DATABASE_PATH" && -s "$DATABASE_PATH" && -w "$DATABASE_PATH" && -w "$(dirname -- "$DATABASE_PATH")" ]] || { log 'Existing writable DB/parent required'; exit 1; }
[[ -d "$UPLOADS_PATH" && -d "$LIVE_DIR" && -d "$KEYS_DIR" && "$KEYS_DIR" == /* ]] || { log 'Persistent folders missing'; exit 1; }
[[ -f "$SERVICE_ENV_FILE" && "$SERVICE_ENV_FILE" == /* ]] || { log 'Explicit runtime EnvironmentFile required'; exit 1; }
SERVICE_ENV_FILE="$(realpath -e -- "$SERVICE_ENV_FILE")"
for folder in "$LIVE_DIR" "$RELEASES_DIR" "$REPOSITORY" "$UPLOADS_PATH"; do
    ! inside "$SERVICE_ENV_FILE" "$folder" || { log 'Runtime secrets must be outside the release/web/source'; exit 1; }
done
KEYS_DIR="$(realpath -e -- "$KEYS_DIR")"
! inside "$KEYS_DIR" "$LIVE_DIR" && ! inside "$KEYS_DIR" "$RELEASES_DIR" && ! inside "$KEYS_DIR" "$REPOSITORY" && ! inside "$KEYS_DIR" "$UPLOADS_PATH" || { log 'Keys in replaceable/public folder'; exit 1; }
FOLDERS=("$LIVE_DIR" "$RELEASES_DIR" "$REPOSITORY" "$UPLOADS_PATH" "$BACKUP_DIR" "$KEYS_DIR")
for ((i=0; i<${#FOLDERS[@]}; i++)); do
    for ((j=i+1; j<${#FOLDERS[@]}; j++)); do
        [[ "${FOLDERS[i]}" != "${FOLDERS[j]}" ]] && ! inside "${FOLDERS[i]}" "${FOLDERS[j]}" && ! inside "${FOLDERS[j]}" "${FOLDERS[i]}" || { log 'Overlapping persistent/application folders'; exit 1; }
    done
done
if [[ "$EVIDENCE_SOURCE" != - ]]; then
    [[ -f "$EVIDENCE_SOURCE" && -s "$EVIDENCE_SOURCE" && "$EVIDENCE_SOURCE" == /* && "$EVIDENCE_SHA" =~ ^[a-fA-F0-9]{64}$ ]] || { log 'Explicit evidence path/hash required'; exit 1; }
    EVIDENCE_SOURCE="$(realpath -e -- "$EVIDENCE_SOURCE")"
    for folder in "$LIVE_DIR" "$RELEASES_DIR" "$REPOSITORY" "$UPLOADS_PATH"; do
        ! inside "$EVIDENCE_SOURCE" "$folder" || { log 'Evidence source in public/replaceable/source folder'; exit 1; }
    done
fi
# BEGIN LOCK GUARD FUNCTIONS
lock_file_is_trusted() {
    local owner mode
    [[ -f "$LOCK_FILE" && ! -L "$LOCK_FILE" && "$(stat -c %h -- "$LOCK_FILE")" == 1 ]] || return 1
    owner="$(stat -c %u -- "$LOCK_FILE")" || return 1
    mode="$(stat -c %a -- "$LOCK_FILE")" || return 1
    [[ "$owner" == 0 || "$owner" == "$(id -u)" ]] && (( (8#$mode & 0022) == 0 ))
}
prepare_deploy_lock() {
    # Windows harness cannot prove POSIX permissions or /proc descriptors.
    if [[ "$OSTYPE" != linux* ]]; then
        [[ -n "${LARAFASHION_DEPLOY_HARNESS_ROOT:-}" && ( "$OSTYPE" == msys || "$OSTYPE" == cygwin ) ]] && return 0
        return 1
    fi
    local parent owner mode
    parent="$(realpath -e -- "$(dirname -- "$LOCK_FILE")")" || return 1
    owner="$(stat -c %u -- "$parent")" || return 1
    mode="$(stat -c %a -- "$parent")" || return 1
    [[ "$owner" == 0 || "$owner" == "$(id -u)" ]] || return 1
    # A shared parent is allowed only when root owns it and sticky semantics protect the file.
    (( (8#$mode & 0022) == 0 || (owner == 0 && (8#$mode & 01000) != 0) )) || return 1
    if [[ ! -e "$LOCK_FILE" && ! -L "$LOCK_FILE" ]]; then
        (set -o noclobber; : > "$LOCK_FILE") || return 1
    fi
    lock_file_is_trusted
}
verify_deploy_lock_fd() {
    if [[ "$OSTYPE" != linux* ]]; then
        [[ -n "${LARAFASHION_DEPLOY_HARNESS_ROOT:-}" && ( "$OSTYPE" == msys || "$OSTYPE" == cygwin ) ]] && return 0
        return 1
    fi
    lock_file_is_trusted && [[ "$(stat -Lc '%d:%i' -- /proc/$$/fd/9)" == "$(stat -c '%d:%i' -- "$LOCK_FILE")" ]]
}
# END LOCK GUARD FUNCTIONS
prepare_deploy_lock || { log 'Untrusted deployment lock or parent'; exit 1; }
# Prove descriptor identity, conflicting independent open, then inherited ownership.
if [[ "${LARAFASHION_DEPLOY_LOCK_HELD:-0}" == 1 ]]; then
    verify_deploy_lock_fd || { log 'Lock descriptor changed'; exit 1; }
    [[ -e /proc/$$/fd/9 && "$(realpath -e -- /proc/$$/fd/9)" == "$LOCK_FILE" ]] || { log 'Invalid inherited descriptor'; exit 1; }
    if flock -n -E 73 "$LOCK_FILE" true; then log 'Inherited lock not held'; exit 1;
    else [[ $? == 73 ]] || { log 'Lock probe failed'; exit 1; }; fi
    flock -n -E 73 9 || { log 'Inherited description does not own lock'; exit 1; }
else
    exec 9<>"$LOCK_FILE"
    verify_deploy_lock_fd || { log 'Lock descriptor changed'; exit 1; }
    flock -w 900 9 || { log 'Another deployment active'; exit 1; }
fi
PROJECT_FILE="$REPOSITORY/LaraFashion/LaraFashion.csproj"
TOOL_PROJECT="$REPOSITORY/LaraFashion.IntegrationTests/LaraFashion.IntegrationTests.csproj"
[[ -f "$PROJECT_FILE" && -f "$TOOL_PROJECT" ]] || { log 'Projects missing'; exit 1; }
cd "$REPOSITORY"
CHECKOUT_SHA="$(git rev-parse HEAD)" || exit 1
CHECKOUT_STATUS="$(git status --porcelain --untracked-files=all)" || exit 1
[[ "$CHECKOUT_SHA" == "$DEPLOY_COMMIT" && -z "$CHECKOUT_STATUS" ]] || { log 'Checkout differs from tested SHA or contains unsaved/untracked files'; exit 1; }
mkdir -p -- "$RELEASES_DIR" "$BACKUP_DIR/deploy-attempts"
[[ -w "$RELEASES_DIR" && -w "$BACKUP_DIR" ]] || { log 'Unwritable folders'; exit 1; }
[[ "$(stat -c %d -- "$LIVE_DIR")" == "$(stat -c %d -- "$RELEASES_DIR")" ]] || { log 'Release rename requires the same filesystem'; exit 1; }
AVAILABLE_KB="$(df -Pk -- "$BACKUP_DIR" | awk 'NR==2 {print $4}')"
DB_BYTES="$(stat -c %s -- "$DATABASE_PATH")"
[[ "$AVAILABLE_KB" =~ ^[0-9]+$ && "$AVAILABLE_KB" -gt $(( DB_BYTES * 6 / 1024 + 1048576 )) ]] || { log 'Insufficient space'; exit 1; }
ATTEMPT_ID="$(date -u +%Y%m%dT%H%M%SZ)-${DEPLOY_COMMIT:0:12}-$$"
ATTEMPT_DIR="$BACKUP_DIR/deploy-attempts/$ATTEMPT_ID"
mkdir -- "$ATTEMPT_DIR"
STAGING_DIR="$RELEASES_DIR/.staging-$ATTEMPT_ID"
ROLLBACK_DIR="$RELEASES_DIR/previous-$ATTEMPT_ID"
FAILED_DIR="$RELEASES_DIR/failed-$ATTEMPT_ID"
for path in "$STAGING_DIR" "$ROLLBACK_DIR" "$FAILED_DIR"; do
    [[ ! -e "$path" && ! -L "$path" ]] || { log 'Attempt destination already exists'; exit 1; }
done
PREVIOUS_ID="$(stat -c '%d:%i' -- "$LIVE_DIR")"
TOOL_DIR="$ATTEMPT_DIR/maintenance"
TOOL_DLL="$TOOL_DIR/LaraFashion.IntegrationTests.dll"
STATE_FILE="$ATTEMPT_DIR/state.json"
PLAN_FILE="$ATTEMPT_DIR/plan.json"
BACKUP_PATH="$ATTEMPT_DIR/before.db"
STOP_ATTEMPTED=0 WRITERS_STOPPED=0 START_ATTEMPTED=0 FILES_SWAP_ATTEMPTED=0 TOOL_READY=0 PREPARED=0 FINISHED=0 RESULT_COUNTER=0
maintenance() {
    local operation="$1"; shift
    RESULT_COUNTER=$((RESULT_COUNTER+1))
    local result="$ATTEMPT_DIR/result-$RESULT_COUNTER.json"
    # A time budget never kills an in-progress schema change or database restore.
    if [[ "$FIRST_TRANSITION" == 1 && ( "$operation" == --deployment-apply || "$operation" == --deployment-restore ) ]]; then
        if ! dotnet "$TOOL_DLL" "$operation" "$@" "$result" >"$ATTEMPT_DIR/command-$RESULT_COUNTER.log"; then log "Failed $operation"; return 1; fi
    elif ! timeout "$OPERATION_TIMEOUT" dotnet "$TOOL_DLL" "$operation" "$@" "$result" >"$ATTEMPT_DIR/command-$RESULT_COUNTER.log"; then log "Failed $operation"; return 1; fi
    if ! timeout "$OPERATION_TIMEOUT" dotnet "$TOOL_DLL" --deployment-check-result "$result" "$operation" "$DEPLOY_COMMIT" >"$ATTEMPT_DIR/check-$RESULT_COUNTER.log"; then log "Rejected result $operation"; return 1; fi
    log "Verified $operation"
}
stage() { maintenance --deployment-state "$STATE_FILE" "$1" "$DEPLOY_COMMIT" "$ROLLBACK_DIR"; }
writers_stopped() {
    local unit active pid rc
    for unit in $WRITER_SERVICES; do
        active="$(sudo systemctl show "$unit" --property=ActiveState --value)" || return 1
        pid="$(sudo systemctl show "$unit" --property=MainPID --value)" || return 1
        [[ ( "$active" == inactive || "$active" == failed ) && "$pid" == 0 ]] || return 1
    done
    if sudo fuser -s -- "$DATABASE_PATH" "${DATABASE_PATH}-wal" "${DATABASE_PATH}-shm"; then return 1;
    else rc=$?; [[ "$rc" == 1 ]] || return 1; fi
    [[ "$FIRST_TRANSITION" == 0 ]] || transition writers
}
stop_writers() {
    local unit
    for unit in $WRITER_SERVICES; do timeout "$SERVICE_TIMEOUT" sudo systemctl stop "$unit" || return 1; done
    writers_stopped
}
start_writers() {
    local unit
    for unit in $WRITER_SERVICES; do timeout "$SERVICE_TIMEOUT" sudo systemctl start "$unit" || return 1; done
}
wait_for_health() {
    local operation="$1" expected="$2" attempt response status
    for attempt in {1..12}; do
        response="$ATTEMPT_DIR/health-$operation-$attempt.json"
        if [[ "$(sudo systemctl show "$SERVICE_NAME" --property=ActiveState --value)" == active ]] \
           && status="$(curl --request GET --fail --silent --show-error --proto '=http,https' --max-time 5 --output "$response" --write-out '%{http_code}' "$HEALTH_URL")" \
           && [[ "$status" == 200 ]] && maintenance "$operation" "$DATABASE_PATH" "$response" "$expected" "$(sudo systemctl show "$SERVICE_NAME" --property=MainPID --value)"; then
            return 0
        fi
        sleep 5
    done
    return 1
}
# No directory nesting, overwrite, cross-device copy, or success on a skipped move.
rename_release() {
    local source="$1" destination="$2" identity
    [[ -d "$source" && ! -L "$source" && ! -e "$destination" && ! -L "$destination" ]] || return 1
    [[ "$(stat -c %d -- "$source")" == "$(stat -c %d -- "$(dirname -- "$destination")")" ]] || return 1
    identity="$(stat -c '%d:%i' -- "$source")" || return 1
    mv -T --no-clobber -- "$source" "$destination" || return 1
    [[ ! -e "$source" && ! -L "$source" && -d "$destination" && ! -L "$destination"
        && "$(stat -c '%d:%i' -- "$destination")" == "$identity" ]]
}
recover() {
    local code="$1"; trap - ERR INT TERM HUP; set +e
    [[ "$FINISHED" == 1 ]] && return
    log "Deployment failed; exit=$code. Private attempt evidence retained."
    if [[ "$TOOL_READY" == 0 || "$PREPARED" == 0 ]]; then exit "$code"; fi
    if [[ "$STOP_ATTEMPTED" == 0 && "$START_ATTEMPTED" == 0 ]]; then
        stage FailedBeforeStop || log 'Recovery state write failed'
        exit "$code"
    fi
    if ! stop_writers; then
        stage ManualRecoveryRequired || log 'Recovery state write failed'
        log 'MANUAL_RECOVERY_REQUIRED: writers not proven stopped; no restore/start.'; exit "$code"
    fi
    if [[ "$START_ATTEMPTED" == 1 ]]; then
        stage ManualRecoveryRequired || log 'Recovery state write failed'
        log 'MANUAL_RECOVERY_REQUIRED: start attempted; current DB retained.'; exit "$code"
    fi
    local previous="$LIVE_DIR"
    [[ -d "$ROLLBACK_DIR" ]] && previous="$ROLLBACK_DIR"
    if [[ "$FIRST_TRANSITION" == 1 ]]; then
        if [[ ! -d "$previous" || -L "$previous" || "$(stat -c '%d:%i' -- "$previous")" != "$PREVIOUS_ID" ]]; then
            stage ManualRecoveryRequired; exit "$code"
        fi
        if ! transition old-untouched; then
            if [[ ! -f "$PLAN_FILE" || ! -f "$BACKUP_PATH" ]] || ! transition legacy-compatible "$previous" "$BACKUP_PATH" \
                || ! maintenance --deployment-restore "$DATABASE_PATH" "$PLAN_FILE"; then
                stage ManualRecoveryRequired; log 'MANUAL_RECOVERY_REQUIRED: legacy recovery pair or original business data unproven'; exit "$code"
            fi
        fi
    elif [[ ! -d "$previous" || -L "$previous" || "$(stat -c '%d:%i' -- "$previous")" != "$PREVIOUS_ID"
        || ! -f "$PLAN_FILE" || ! -f "$BACKUP_PATH" || ! -f "$previous/release-version.json" ]] \
       || ! maintenance --deployment-compatible "$previous/release-version.json" "$BACKUP_PATH" \
       || ! maintenance --deployment-restore "$DATABASE_PATH" "$PLAN_FILE"; then
        stage ManualRecoveryRequired || log 'Recovery state write failed'
        log 'MANUAL_RECOVERY_REQUIRED: recovery pair/business preservation not proven.'; exit "$code"
    fi
    if [[ "$FILES_SWAP_ATTEMPTED" == 1 && -d "$ROLLBACK_DIR" ]]; then
        if [[ -d "$LIVE_DIR" ]]; then
            if [[ -L "$LIVE_DIR" || "$(stat -c '%d:%i' -- "$LIVE_DIR")" != "$CANDIDATE_ID" ]] || ! rename_release "$LIVE_DIR" "$FAILED_DIR"; then
                stage ManualRecoveryRequired || log 'Recovery state write failed'
                log 'MANUAL_RECOVERY_REQUIRED: candidate location changed or failed destination occupied'; exit "$code"
            fi
        fi
        if ! rename_release "$ROLLBACK_DIR" "$LIVE_DIR"; then
            stage ManualRecoveryRequired || log 'Recovery state write failed'
            log 'MANUAL_RECOVERY_REQUIRED: previous app rename refused'; exit "$code"
        fi
    fi
    [[ "$FIRST_TRANSITION" == 0 ]] || transition restore-config || { stage ManualRecoveryRequired; exit "$code"; }
    START_ATTEMPTED=1
    stage RecoveryStartAttempted || { log 'Recovery intent write failed'; exit "$code"; }
    if ! start_writers || { if [[ "$FIRST_TRANSITION" == 1 ]]; then ! transition legacy-health; else ! wait_for_health --deployment-recovery-health "$LIVE_DIR/release-version.json"; fi; }; then
        if ! stop_writers; then log 'Recovery writers not proven stopped'; fi
        stage ManualRecoveryRequired || log 'Recovery state write failed'
        log 'MANUAL_RECOVERY_REQUIRED: recovery start or typed health failed; current DB retained'; exit "$code"
    fi
    stage Recovered || { log 'Recovery result write failed'; exit "$code"; }
    log 'Previous verified pair recovered; deployment remains failed.'
    exit "$code"
}
trap 'recover $?' ERR
trap 'recover 130' INT
trap 'recover 143' TERM
trap 'recover 129' HUP
if [[ "$FIRST_TRANSITION" == 1 ]]; then
    transition claim
    # Every exit consumes/closes this authorization; a reconnect only inspects its existing state.
    trap 'transition close >/dev/null || log "Authorization closure requires inspection"' EXIT
    [[ -f "$TRANSITION_ROOT/legacy-recovery.json" && -f "$TRANSITION_ROOT/runtime.env" ]] || fail 'First transition preparation incomplete'
    transition verify-receipt practice
    transition legacy-health
fi
for unit in $WRITER_SERVICES; do
    [[ "$unit" =~ ^[a-zA-Z0-9_.@-]+$ ]] || fail 'Invalid writer unit'
    [[ "$(sudo systemctl show "$unit" --property=LoadState --value)" == loaded ]] || fail 'Writer unavailable'
done
[[ "$(sudo systemctl show "$SERVICE_NAME" --property=WorkingDirectory --value)" == "$LIVE_DIR" ]] || fail 'WorkingDirectory mismatch'
ENV_FILES="$(sudo systemctl show "$SERVICE_NAME" --property=EnvironmentFiles --value)"
if [[ "$FIRST_TRANSITION" == 1 ]]; then
    [[ -z "$ENV_FILES" ]] || fail 'Legacy EnvironmentFiles changed before activation'
else
    [[ "$ENV_FILES" == "$SERVICE_ENV_FILE" || "$ENV_FILES" == "$SERVICE_ENV_FILE (ignore_errors=no)" ]] || fail 'Service must load the verified runtime EnvironmentFile only'
fi
EXEC_START="$(sudo systemctl show "$SERVICE_NAME" --property=ExecStart --value)"
[[ "$EXEC_START" == *"$LIVE_DIR/LaraFashion"* || "$EXEC_START" == *"$LIVE_DIR/LaraFashion.dll"* ]] || fail 'Executable mismatch'
SERVICE_USER="$(sudo systemctl show "$SERVICE_NAME" --property=User --value)"
SERVICE_USER="${SERVICE_USER:-root}"
for path in "$DATABASE_PATH" "$(dirname -- "$DATABASE_PATH")" "$UPLOADS_PATH" "$KEYS_DIR"; do
    sudo -u "$SERVICE_USER" test -r "$path" && sudo -u "$SERVICE_USER" test -w "$path" || fail 'Persistent permissions failed'
    if [[ -d "$path" ]]; then sudo -u "$SERVICE_USER" test -x "$path" || fail 'Persistent directory traversal denied'; fi
done
log 'Building candidate and private maintenance tool before writer stop.'
timeout "$BUILD_TIMEOUT" dotnet tool restore >"$ATTEMPT_DIR/tool-restore.log"
timeout "$BUILD_TIMEOUT" dotnet restore "$TOOL_PROJECT" >"$ATTEMPT_DIR/restore.log"
timeout "$BUILD_TIMEOUT" dotnet build "$TOOL_PROJECT" -c Release --no-restore -p:SourceRevisionId="$DEPLOY_COMMIT" >"$ATTEMPT_DIR/build.log"
LARAFASHION_EF_CONNECTION="Data Source=$ATTEMPT_DIR/design-only.db;Mode=ReadWrite" timeout "$BUILD_TIMEOUT" dotnet tool run dotnet-ef migrations has-pending-model-changes --project "$PROJECT_FILE" --startup-project "$PROJECT_FILE" --configuration Release --no-build >"$ATTEMPT_DIR/model.log"
timeout "$BUILD_TIMEOUT" dotnet publish "$PROJECT_FILE" -c Release --no-restore --no-build -o "$STAGING_DIR" >"$ATTEMPT_DIR/publish.log"
timeout "$BUILD_TIMEOUT" dotnet publish "$TOOL_PROJECT" -c Release --no-restore --no-build -o "$TOOL_DIR" >"$ATTEMPT_DIR/maintenance-publish.log"
[[ -f "$STAGING_DIR/LaraFashion.dll" && -f "$TOOL_DLL" && -f "$TOOL_DIR/LaraFashion.IntegrationTests.runtimeconfig.json" ]] || fail 'Published tool/runtime missing'
[[ -d "$STAGING_DIR" && ! -L "$STAGING_DIR" ]] || fail 'Untrusted candidate directory'
CANDIDATE_ID="$(stat -c '%d:%i' -- "$STAGING_DIR")"
TOOL_READY=1
maintenance --maintenance-contract
maintenance --deployment-config "$SERVICE_ENV_FILE" "$DATABASE_PATH" "$UPLOADS_PATH" "$KEYS_DIR"
maintenance --deployment-package "$STAGING_DIR"
maintenance --deployment-manifest "$STAGING_DIR"
[[ "$FIRST_TRANSITION" == 0 ]] || maintenance --deployment-dp-probe "$KEYS_DIR" "$LIVE_DIR"
sudo -u "$SERVICE_USER" test -r "$STAGING_DIR/LaraFashion.dll" || fail 'Service cannot read candidate before stop'
while IFS= read -r -d '' previous_state; do
    if ! maintenance --deployment-previous-attempt "$previous_state"; then
        if ! maintenance --deployment-recovery-inspect "$previous_state" "$DATABASE_PATH"; then log 'Previous schema needs independent inspection'; fi
        fail 'Unresolved previous attempt; no automatic resume/start'
    fi
done < <(find "$BACKUP_DIR/deploy-attempts" -mindepth 2 -maxdepth 2 -type f -name state.json -print0)
stage Prepared
PREPARED=1
[[ "$FIRST_TRANSITION" == 0 ]] || transition deadline 3600
STOP_ATTEMPTED=1
stage WritersStopAttempted
stop_writers || fail 'Cannot prove writers stopped'
WRITERS_STOPPED=1
stage WritersStopped
[[ "$FIRST_TRANSITION" == 0 ]] || transition cutoff
maintenance --deployment-backup "$DATABASE_PATH" "$BACKUP_PATH"
if [[ "$FIRST_TRANSITION" == 1 ]]; then
    transition recovery-package "$BACKUP_PATH"
    log "Cutover encrypted recovery package ready: $TRANSITION_RUN_ID; awaiting verified private external copy"
    RECEIPT_READY=0
    for _ in {1..120}; do
        transition deadline 2400 || fail 'Insufficient time for external recovery verification and recovery reserve'
        if [[ -f "$TRANSITION_ROOT/cutover-external-verified.json" ]] && transition verify-receipt cutover; then RECEIPT_READY=1; break; fi
        sleep 5
    done
    [[ "$RECEIPT_READY" == 1 ]] || fail 'External recovery package was not verified; no schema change allowed'
fi
stage BackupVerified
maintenance --deployment-plan "$BACKUP_PATH" "$EVIDENCE_SOURCE" "$EVIDENCE_SHA" "$PLAN_FILE"
stage PlanVerified
maintenance --deployment-freeze "$DATABASE_PATH" "$PLAN_FILE"
stage TargetFrozen
maintenance --verify-review-upgrade --plan "$PLAN_FILE"
stage RehearsalVerified
writers_stopped || fail 'Writer appeared during rehearsal'
stage EvidencePrepareAttempted
[[ "$FIRST_TRANSITION" == 0 ]] || transition deadline 2400
maintenance --prepare-legacy-review --plan "$PLAN_FILE" "$DATABASE_PATH"
stage EvidenceVerified
writers_stopped || fail 'Writer appeared before migration'
stage SchemaChangeAttempted
[[ "$FIRST_TRANSITION" == 0 ]] || transition deadline 2400
maintenance --deployment-apply "$DATABASE_PATH" "$PLAN_FILE"
maintenance --deployment-verify "$DATABASE_PATH" "$PLAN_FILE"
stage SchemaVerified
writers_stopped || fail 'Writer appeared before swap'
FILES_SWAP_ATTEMPTED=1
stage FilesSwapAttempted
[[ "$FIRST_TRANSITION" == 0 ]] || transition deadline 2100
[[ "$(stat -c '%d:%i' -- "$LIVE_DIR")" == "$PREVIOUS_ID" && "$(stat -c '%d:%i' -- "$STAGING_DIR")" == "$CANDIDATE_ID" ]] || fail 'Release directories changed during preparation'
rename_release "$LIVE_DIR" "$ROLLBACK_DIR" || fail 'Previous release rename refused'
rename_release "$STAGING_DIR" "$LIVE_DIR" || fail 'Candidate release rename refused'
stage FilesSwapped
[[ "$FIRST_TRANSITION" == 0 ]] || transition activate
START_ATTEMPTED=1
stage ApplicationStartAttempted
start_writers || fail 'Start failed; do not assume no writes occurred'
wait_for_health --deployment-health "$DEPLOY_COMMIT" || fail 'Health failed/wrong release/database'
if [[ "$FIRST_TRANSITION" == 1 ]]; then
    transition candidate-check
    maintenance --deployment-verify "$DATABASE_PATH" "$PLAN_FILE"
    transition verify-uploads
    transition open
    transition post-backup
fi
stage HealthVerified
stage Completed
FINISHED=1
trap - ERR INT TERM HUP
log 'Completed: candidate health/schema verified; no business reconciliation.'
# No age/count retention removes attempt backups, eligibility sources, recovery releases,
# or key directories. Pruning needs an independent operator-reviewed policy.
