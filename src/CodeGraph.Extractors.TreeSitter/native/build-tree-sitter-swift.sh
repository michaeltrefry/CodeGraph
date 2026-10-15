#!/usr/bin/env sh
# Builds the native tree-sitter Swift grammar (libtree-sitter-swift) from pinned,
# hash-verified generated C sources.
#
# Usage: build-tree-sitter-swift.sh <output-file>
#   <output-file>  path of the shared library to produce, e.g. .../libtree-sitter-swift.so
#
# The TreeSitter.DotNet package does not ship a Swift grammar, so the extractor build
# (CodeGraph.Extractors.TreeSitter.csproj) and the Docker images compile it here. The
# release tarball of alex-pinkus/tree-sitter-swift carries the generated parser.c that the
# repository's main branch does not commit. Nothing is downloaded at runtime.
#
# To move to a new grammar version, update both values below together, check the new
# parser.c LANGUAGE_VERSION is supported by the bundled tree-sitter runtime (TreeSitter.DotNet
# 1.3.0 = tree-sitter 0.26.3, ABI 13..15), and bump LanguageSpecs.Swift.ExtractionVersion.
set -eu

SWIFT_GRAMMAR_VERSION="0.7.3"
SWIFT_GRAMMAR_SHA256="c595b41459b0816f246ec27f60e08392ff453135e12ef47a1852ed37fe6705fe"
SWIFT_GRAMMAR_URL="https://github.com/alex-pinkus/tree-sitter-swift/releases/download/${SWIFT_GRAMMAR_VERSION}/tree-sitter-swift.tar.gz"

if [ "$#" -ne 1 ] || [ -z "$1" ]; then
  echo "usage: $0 <output-file>" >&2
  exit 2
fi

output="$1"
output_dir="$(dirname "$output")"
mkdir -p "$output_dir"

cc_bin="${CC:-cc}"
if ! command -v "$cc_bin" >/dev/null 2>&1; then
  echo "error: a C compiler ('$cc_bin') is required to build the tree-sitter Swift grammar." >&2
  echo "       Install one (Xcode command line tools, build-essential/gcc) or set CC." >&2
  exit 1
fi

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

work="$(mktemp -d "${TMPDIR:-/tmp}/tree-sitter-swift.XXXXXX")"
trap 'rm -rf "$work"' EXIT INT TERM

archive="$work/tree-sitter-swift.tar.gz"
echo "Fetching tree-sitter-swift ${SWIFT_GRAMMAR_VERSION}"
curl -fsSL --retry 3 "$SWIFT_GRAMMAR_URL" -o "$archive"

actual="$(sha256_of "$archive")"
if [ "$actual" != "$SWIFT_GRAMMAR_SHA256" ]; then
  echo "error: tree-sitter-swift ${SWIFT_GRAMMAR_VERSION} sha256 mismatch" >&2
  echo "       expected ${SWIFT_GRAMMAR_SHA256}" >&2
  echo "       actual   ${actual}" >&2
  exit 1
fi

mkdir -p "$work/src"
tar -xzf "$archive" -C "$work/src" src/parser.c src/scanner.c src/tree_sitter

echo "Compiling $(basename "$output") with ${cc_bin}"
"$cc_bin" -shared -fPIC -O2 -std=c11 \
  -I "$work/src/src" \
  "$work/src/src/parser.c" "$work/src/src/scanner.c" \
  -o "$work/$(basename "$output")"

mv -f "$work/$(basename "$output")" "$output"
echo "Built $output"
