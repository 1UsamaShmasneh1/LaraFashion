set +x
set -Eeuo pipefail
umask 077
[[ "$DEPLOY_COMMIT" =~ ^[0-9a-f]{40}$ ]]
[[ "$LARAFASHION_TRANSITION_RUN_ID" =~ ^lf-cutover-[A-Za-z0-9-]+$ ]]
[[ "$AUTHORIZED_UNTIL_UTC" == 2026-10-11T23:38:00Z ]]
(( $(date -u -d "$AUTHORIZED_UNTIL_UTC" +%s) - $(date -u +%s) >= 3600 ))
LOCK_FILE=/tmp/larafashion-production-deploy.lock
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
prepare_deploy_lock || { echo "Untrusted deployment lock or parent" >&2; exit 1; }
exec 9<>"$LOCK_FILE"
verify_deploy_lock_fd || { echo "Lock descriptor changed" >&2; exit 1; }
flock -w 900 9
REPOSITORY=/var/www/larafashion/LaraFashion
cd "$REPOSITORY"
[[ "$(realpath -e -- "$(git rev-parse --show-toplevel)")" == "$(realpath -e -- "$REPOSITORY")" ]]
# BEGIN SAFE CHECKOUT FUNCTIONS
require_clean_checkout() {
    local status
    status="$(git status --porcelain --untracked-files=all)" || return 1
    [[ -z "$status" ]] || { echo 'Unsaved or untracked server files; checkout refused' >&2; return 1; }
    local operation
    for operation in MERGE_HEAD CHERRY_PICK_HEAD REVERT_HEAD rebase-merge rebase-apply sequencer; do
        [[ ! -e "$(git rev-parse --git-path "$operation")" ]] || { echo 'Unfinished Git operation; checkout refused' >&2; return 1; }
    done
}
checkout_candidate() {
    local candidate="$1"
    [[ "$candidate" =~ ^[0-9a-f]{40}$ ]] || return 1
    require_clean_checkout || return 1
    git cat-file -e "${candidate}^{commit}" || return 1
    git merge-base --is-ancestor "$candidate" refs/remotes/origin/main || return 1
    git -c core.hooksPath=/dev/null checkout --detach --no-overwrite-ignore "$candidate" || return 1
    [[ "$(git rev-parse HEAD)" == "$candidate" ]] && require_clean_checkout
}
# END SAFE CHECKOUT FUNCTIONS
require_clean_checkout
git fetch --no-tags origin '+refs/heads/main:refs/remotes/origin/main'
checkout_candidate "$DEPLOY_COMMIT"
export LARAFASHION_DEPLOY_LOCK_HELD=1
bash deploy/deploy-production.sh "$DEPLOY_COMMIT"
