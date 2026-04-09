#!/bin/sh

set -eu

HOOK_NAME="${1:-}"

if [ -z "$HOOK_NAME" ]; then
  echo "usage: $0 <post-commit|post-merge>" >&2
  exit 1
fi

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
CURRENT_BRANCH="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")"
MAIN_BRANCH="${CODEGRAPH_HOOK_MAIN_BRANCH:-main}"

if [ "$CURRENT_BRANCH" != "$MAIN_BRANCH" ]; then
  exit 0
fi

API_URL="${CODEGRAPH_API_URL:-http://localhost:5037}"
REPO_NAME="${CODEGRAPH_HOOK_REPO_NAME:-$(basename "$REPO_ROOT")}"
REPO_ENTRY="${REPO_NAME}::${REPO_ROOT}"

SHOULD_ANALYZE="false"
case "$HOOK_NAME" in
  post-commit)
    SHOULD_ANALYZE="false"
    ;;
  post-merge)
    SHOULD_ANALYZE="true"
    ;;
  *)
    echo "unsupported hook name: $HOOK_NAME" >&2
    exit 1
    ;;
esac

PAYLOAD=$(cat <<EOF
{"repos":["$REPO_ENTRY"],"shouldIndex":true,"shouldAnalyze":$SHOULD_ANALYZE,"skipIfUpToDate":false,"includeAllSource":false}
EOF
)

curl \
  --silent \
  --show-error \
  --max-time "${CODEGRAPH_HOOK_TIMEOUT_SECONDS:-5}" \
  -X POST \
  "$API_URL/api/settings/processRepos" \
  -H "Content-Type: application/json" \
  -d "$PAYLOAD" \
  >/dev/null 2>&1 &

exit 0
