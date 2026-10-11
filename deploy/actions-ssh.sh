#!/usr/bin/env bash
# Actions transport only; the remote wrapper owns FD9 and the coordinated deployment.
set +x
set -Eeuo pipefail
umask 077
[[ "${DEPLOY_COMMIT:-}" =~ ^[0-9a-f]{40}$ ]]
[[ "${LARAFASHION_TRANSITION_RUN_ID:-}" =~ ^lf-cutover-[A-Za-z0-9-]+$ ]]
[[ "${LARAFASHION_ACTION_RUN_ID:-}" =~ ^[0-9]+$ ]]
[[ "${LARAFASHION_FIRST_TRANSITION:-}" == 1 ]]
[[ "${AUTHORIZED_UNTIL_UTC:-}" == 2026-10-11T23:38:00Z ]]
[[ "${SSH_HOST_FINGERPRINT:-}" == SHA256:PY1p4k7jJ1WXl+Mz2ze1wUFQX9oEfM4YKsBAO6gRKws ]]
(( $(date -u -d "$AUTHORIZED_UNTIL_UTC" +%s) - $(date -u +%s) >= 3600 ))
[[ -n "${SSH_KEY:-}" && -d "${RUNNER_TEMP:-}" ]]
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
[[ -f "$SCRIPT_DIR/actions-remote.sh" ]]
SSH_TMP="$(mktemp -d "$RUNNER_TEMP/larafashion-ssh.XXXXXX")"
cleanup() {
    # Remove only this step's three credential/config files, never a shared SSH directory.
    rm -f -- "$SSH_TMP/identity" "$SSH_TMP/known_hosts" "$SSH_TMP/config"
    rmdir -- "$SSH_TMP"
}
trap cleanup EXIT
printf '%s\n' "$SSH_KEY" | tr -d '\r' >"$SSH_TMP/identity"
unset SSH_KEY
chmod 600 "$SSH_TMP/identity"
printf '%s\n' '82.70.208.68 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIO0B5HMo3gyvxOC57ahPy/5IU0C5mNYN1SLHrNuzChoS' >"$SSH_TMP/known_hosts"
[[ "$(ssh-keygen -lf "$SSH_TMP/known_hosts" -E sha256 | awk '{print $2}')" == "$SSH_HOST_FINGERPRINT" ]]
cat >"$SSH_TMP/config" <<EOF
Host 82.70.208.68
    HostName 82.70.208.68
    Port 22
    User ubuntu
    IdentityFile "$SSH_TMP/identity"
    UserKnownHostsFile "$SSH_TMP/known_hosts"
    GlobalKnownHostsFile /dev/null
    StrictHostKeyChecking yes
    HostKeyAlgorithms ssh-ed25519
    UpdateHostKeys no
    BatchMode yes
    IdentitiesOnly yes
    IdentityAgent none
    PreferredAuthentications publickey
    PubkeyAuthentication yes
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    GSSAPIAuthentication no
    HostbasedAuthentication no
    ForwardAgent no
    ForwardX11 no
    ClearAllForwardings yes
    Tunnel no
    ControlMaster no
    ControlPath none
    ControlPersist no
    ProxyCommand none
    ProxyJump none
    PermitLocalCommand no
    RequestTTY no
    ConnectTimeout 30
    ConnectionAttempts 1
    ServerAliveInterval 15
    ServerAliveCountMax 3
EOF
# Fixed, validated non-secret fields only. The private key stays on this ephemeral runner.
{
    for name in DEPLOY_COMMIT LARAFASHION_FIRST_TRANSITION LARAFASHION_TRANSITION_RUN_ID LARAFASHION_ACTION_RUN_ID AUTHORIZED_UNTIL_UTC; do
        printf 'export %s=%q\n' "$name" "${!name}"
    done
    cat -- "$SCRIPT_DIR/actions-remote.sh"
} | ssh -F "$SSH_TMP/config" 82.70.208.68 bash -s
