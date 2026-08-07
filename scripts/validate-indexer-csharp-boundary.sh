#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image_tag="codegraph-indexer-security-validation:local"
fixture_dir="$(mktemp -d)"
validation_id="${RANDOM}-$$"
network_name="codegraph-validation-${validation_id}"
mariadb_name="codegraph-validation-mariadb-${validation_id}"
rabbitmq_name="codegraph-validation-rabbitmq-${validation_id}"
cleanup() {
  docker rm -f "${mariadb_name}" "${rabbitmq_name}" >/dev/null 2>&1 || true
  docker network rm "${network_name}" >/dev/null 2>&1 || true
  rm -rf "${fixture_dir}"
}
trap cleanup EXIT

mkdir -p "${fixture_dir}/MaliciousRestore"
cp -R "${repo_root}/src/CodeGraph.Tests/Fixtures/MaliciousRestore/." "${fixture_dir}/MaliciousRestore/"

docker build \
  --file "${repo_root}/Dockerfile.indexer" \
  --tag "${image_tag}" \
  "${repo_root}"

docker network create "${network_name}" >/dev/null
docker run -d --name "${mariadb_name}" --network "${network_name}" \
  --env MARIADB_ROOT_PASSWORD=validation-root \
  --env MARIADB_DATABASE=codegraph \
  --env MARIADB_USER=codegraph \
  --env MARIADB_PASSWORD=validation-password \
  mariadb:11.4 >/dev/null
docker run -d --name "${rabbitmq_name}" --network "${network_name}" \
  --env RABBITMQ_DEFAULT_USER=codegraph \
  --env RABBITMQ_DEFAULT_PASS=validation-password \
  rabbitmq:4.1 >/dev/null

for _ in $(seq 1 60); do
  if docker exec "${mariadb_name}" mariadb-admin ping -h 127.0.0.1 -uroot -pvalidation-root --silent >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
docker exec "${mariadb_name}" mariadb-admin ping -h 127.0.0.1 -uroot -pvalidation-root --silent >/dev/null

for _ in $(seq 1 60); do
  if docker exec "${rabbitmq_name}" rabbitmq-diagnostics -q ping >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
docker exec "${rabbitmq_name}" rabbitmq-diagnostics -q ping >/dev/null

run_boundary_probe() {
  local trusted_identities="$1"
  docker run --rm --network "${network_name}" \
    --env ASPNETCORE_ENVIRONMENT=Production \
    --env CODEGRAPH_SKIP_TS_SIDECAR_WARMUP=true \
    --env CodeGraph__StorageOptions__Provider=MariaDb \
    --env "CodeGraph__StorageOptions__MariaDbConnectionString=Server=${mariadb_name};Port=3306;Database=codegraph;User ID=codegraph;Password=validation-password;" \
    --env CodeGraph__StorageOptions__MariaDbMigrationsPath=/app/sql/migrations \
    --env "CodeGraph__RabbitMqOptions__Host=${rabbitmq_name}" \
    --env CodeGraph__RabbitMqOptions__Username=codegraph \
    --env CodeGraph__RabbitMqOptions__Password=validation-password \
    --env CodeGraph__RepositorySource__Provider=Folder \
    --env CodeGraph__RepositorySource__Folder__RootPath=/validation \
    --env "CodeGraph__IndexingOptions__TrustedDotnetRepositories=${trusted_identities}" \
    --volume "${fixture_dir}:/validation" \
    "${image_tag}" \
    --validate-untrusted-csharp-boundary \
    /validation/MaliciousRestore/MaliciousRestore.slnx \
    /validation/MaliciousRestore/restore-payload-executed.txt
}

run_boundary_probe ""
run_boundary_probe "folder:some-other-repository"

test ! -e "${fixture_dir}/MaliciousRestore/restore-payload-executed.txt"

indexer_config="$(docker compose \
  --file "${repo_root}/docker-compose.yml" \
  --file "${repo_root}/deploy/docker-compose.production.yml" \
  config | awk '/^  codegraph-indexer:/{found=1; next} found && /^  [^ ]/{exit} found{print}')"
if ! grep -Fxq '      CodeGraph__IndexingOptions__TrustedDotnetRepositories: ""' <<<"${indexer_config}"; then
  echo "FAIL: production compose must default trusted .NET repositories to empty." >&2
  exit 1
fi

echo "PASS: production image and compose default enforce the untrusted C# tooling boundary."
