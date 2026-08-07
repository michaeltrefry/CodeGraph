#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image_tag="codegraph-indexer-security-validation:local"
fixture_dir="$(mktemp -d)"
trap 'rm -rf "${fixture_dir}"' EXIT

cp -R "${repo_root}/src/CodeGraph.Tests/Fixtures/MaliciousRestore/." "${fixture_dir}/"

docker build \
  --file "${repo_root}/Dockerfile.indexer" \
  --tag "${image_tag}" \
  "${repo_root}"

docker run --rm \
  --volume "${fixture_dir}:/fixture" \
  "${image_tag}" \
  --validate-untrusted-csharp-boundary \
  /fixture/MaliciousRestore.slnx \
  /fixture/restore-payload-executed.txt

test ! -e "${fixture_dir}/restore-payload-executed.txt"

default_config="$(docker compose \
  --file "${repo_root}/docker-compose.yml" \
  --file "${repo_root}/deploy/docker-compose.production.yml" \
  config)"
if ! grep -Fq 'CodeGraph__IndexingOptions__TrustedDotnetRepositories: ""' <<<"${default_config}"; then
  echo "FAIL: production compose must default trusted .NET repositories to empty." >&2
  exit 1
fi

echo "PASS: production image and compose default enforce the untrusted C# tooling boundary."
