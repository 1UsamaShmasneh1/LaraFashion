set +x
set -Eeuo pipefail
[[ "$DEPLOY_COMMIT" =~ ^[0-9a-f]{40}$ ]]
[[ "$LARAFASHION_TRANSITION_RUN_ID" =~ ^lf-cutover-[A-Za-z0-9-]+$ ]]
[[ "$LARAFASHION_ACTION_RUN_ID" =~ ^[0-9]+$ ]]
[[ "$LARAFASHION_AUTHORIZATION_MODE" == UntilTransitionClosed ]]
sudo --preserve-env=LARAFASHION_ACTION_RUN_ID,LARAFASHION_AUTHORIZATION_MODE python3 -B \
    "/var/www/larafashion/backups/first-transition/$LARAFASHION_TRANSITION_RUN_ID/first-transition.py" \
    transport-preflight "$LARAFASHION_TRANSITION_RUN_ID" "$DEPLOY_COMMIT"
