#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT_DIR"

if [[ -f "$ROOT_DIR/.env" ]]; then
  set -a
  source "$ROOT_DIR/.env"
  set +a
fi

SOURCE_VOLUME="${SOURCE_VOLUME:-memorygraph_neo4j_data}"
SOURCE_USER="${SOURCE_USER:-neo4j}"
SOURCE_PASSWORD="${SOURCE_PASSWORD:-memorygraph}"
SOURCE_DATABASE="${SOURCE_DATABASE:-neo4j}"
SOURCE_USERNAME="${SOURCE_USERNAME:-michael}"

TARGET_URI="${TARGET_URI:-bolt://localhost:7687}"
TARGET_USER="${TARGET_USER:-${CodeGraph__StorageOptions__Neo4jUsername:-neo4j}}"
TARGET_PASSWORD="${TARGET_PASSWORD:-${CodeGraph__StorageOptions__Neo4jPassword:-codegraph}}"
TARGET_DATABASE="${TARGET_DATABASE:-${CodeGraph__StorageOptions__Neo4jDatabase:-neo4j}}"

TEMP_CONTAINER="${TEMP_CONTAINER:-codegraph-memorygraph-source}"
TEMP_BOLT_PORT="${TEMP_BOLT_PORT:-17687}"
TEMP_HTTP_PORT="${TEMP_HTTP_PORT:-17474}"

EXTRA_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --apply|--prefix-ids-with-username|--allow-target-overlap)
      EXTRA_ARGS+=("$1")
      shift
      ;;
    --source-username)
      SOURCE_USERNAME="$2"
      shift 2
      ;;
    --source-volume)
      SOURCE_VOLUME="$2"
      shift 2
      ;;
    --source-password)
      SOURCE_PASSWORD="$2"
      shift 2
      ;;
    --target-uri)
      TARGET_URI="$2"
      shift 2
      ;;
    --target-user)
      TARGET_USER="$2"
      shift 2
      ;;
    --target-password)
      TARGET_PASSWORD="$2"
      shift 2
      ;;
    --target-database)
      TARGET_DATABASE="$2"
      shift 2
      ;;
    --batch-size)
      EXTRA_ARGS+=("$1" "$2")
      shift 2
      ;;
    --help|-h)
      cat <<'EOF'
Usage:
  ./tools/memory-migration/run-memorygraph-migration.sh [options]

Options:
  --apply                         Write migrated data into CodeGraph (default is dry-run)
  --source-username <username>    Legacy MemoryGraph username to migrate (default: michael)
  --source-volume <volume>        Docker volume containing the old Neo4j data (default: memorygraph_neo4j_data)
  --source-password <password>    Legacy Neo4j password (default: memorygraph)
  --target-uri <uri>              CodeGraph Neo4j Bolt URI (default: bolt://localhost:7687)
  --target-user <user>            CodeGraph Neo4j username (default: from .env or neo4j)
  --target-password <password>    CodeGraph Neo4j password (default: from .env or codegraph)
  --target-database <db>          CodeGraph Neo4j database (default: from .env or neo4j)
  --prefix-ids-with-username      Namespace migrated ids with the legacy username
  --allow-target-overlap          Allow migration when target ids already overlap
  --batch-size <n>                Batch size for writes (default from tool: 250)

Notes:
  - This script starts a temporary Neo4j container against the old MemoryGraph volume.
  - It tears the temp container down automatically on exit.
EOF
      exit 0
      ;;
    *)
      echo "error: unknown argument '$1'" >&2
      exit 1
      ;;
  esac
done

cleanup() {
  docker rm -f "$TEMP_CONTAINER" >/dev/null 2>&1 || true
}

trap cleanup EXIT

cleanup

docker run -d --rm \
  --name "$TEMP_CONTAINER" \
  -p "${TEMP_HTTP_PORT}:7474" \
  -p "${TEMP_BOLT_PORT}:7687" \
  -v "${SOURCE_VOLUME}:/data" \
  -e "NEO4J_AUTH=${SOURCE_USER}/${SOURCE_PASSWORD}" \
  neo4j:5-community >/dev/null

READY=0
for _ in $(seq 1 60); do
  if docker exec "$TEMP_CONTAINER" /var/lib/neo4j/bin/cypher-shell -u "$SOURCE_USER" -p "$SOURCE_PASSWORD" "RETURN 1" >/dev/null 2>&1; then
    READY=1
    break
  fi
  sleep 2
done

if [[ "$READY" -ne 1 ]]; then
  echo "error: temporary source Neo4j did not become ready in time." >&2
  docker logs "$TEMP_CONTAINER" >&2 || true
  exit 1
fi

dotnet run --project "$ROOT_DIR/tools/memory-migration/CodeGraph.MemoryMigration.csproj" -- \
  --source-uri "bolt://localhost:${TEMP_BOLT_PORT}" \
  --source-user "$SOURCE_USER" \
  --source-password "$SOURCE_PASSWORD" \
  --source-database "$SOURCE_DATABASE" \
  --source-username "$SOURCE_USERNAME" \
  --target-uri "$TARGET_URI" \
  --target-user "$TARGET_USER" \
  --target-password "$TARGET_PASSWORD" \
  --target-database "$TARGET_DATABASE" \
  "${EXTRA_ARGS[@]}"
