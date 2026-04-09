#!/bin/sh

set -eu

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
HOOKS_DIR="$REPO_ROOT/.githooks"
MAIN_BRANCH="${CODEGRAPH_HOOK_MAIN_BRANCH:-main}"

if [ ! -d "$HOOKS_DIR" ]; then
  echo "Missing hooks directory: $HOOKS_DIR" >&2
  exit 1
fi

chmod +x \
  "$HOOKS_DIR/codegraph-trigger.sh" \
  "$HOOKS_DIR/post-commit" \
  "$HOOKS_DIR/post-merge"

git -C "$REPO_ROOT" config core.hooksPath .githooks

echo "Git hooks installed for $REPO_ROOT"
echo "core.hooksPath=$(git -C "$REPO_ROOT" config --get core.hooksPath)"
echo "post-commit: index only on branch $MAIN_BRANCH"
echo "post-merge: index and analyze on branch $MAIN_BRANCH"
