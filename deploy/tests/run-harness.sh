#!/usr/bin/env bash
set -Eeuo pipefail
export PATH="/usr/bin:/bin:$PATH"
ROOT="$(cd -- "$(dirname -- "$0")/../.." && pwd)"
PYTHON="${LARAFASHION_HARNESS_PYTHON:-python3}"
command -v "$PYTHON" >/dev/null
exec "$PYTHON" "$ROOT/deploy/tests/harness.py" "$ROOT"
