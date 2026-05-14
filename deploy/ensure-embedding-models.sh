#!/bin/sh
set -eu

read_dotenv_value() {
  name="$1"

  if [ ! -f .env ]; then
    return
  fi

  line="$(grep "^$name=" .env 2>/dev/null | tail -n 1 || true)"
  if [ -z "$line" ]; then
    return
  fi

  value="${line#*=}"
  case "$value" in
    \"*\") value="${value#\"}"; value="${value%\"}" ;;
    \'*\') value="${value#\'}"; value="${value%\'}" ;;
  esac

  printf '%s' "$value"
}

model_name="${CODEGRAPH_EMBEDDING_MODEL_NAME:-$(read_dotenv_value CODEGRAPH_EMBEDDING_MODEL_NAME)}"
model_name="${model_name:-nomic-embed-text-v1.5}"

models_root="${CODEGRAPH_DOCKER_MODELS_MOUNT:-$(read_dotenv_value CODEGRAPH_DOCKER_MODELS_MOUNT)}"
models_root="${models_root:-./.cache/models}"
model_dir="${models_root%/}/embeddings/$model_name"

model_url="${CODEGRAPH_EMBEDDING_MODEL_ONNX_URL:-$(read_dotenv_value CODEGRAPH_EMBEDDING_MODEL_ONNX_URL)}"
model_url="${model_url:-https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/onnx/model.onnx?download=true}"

vocab_url="${CODEGRAPH_EMBEDDING_MODEL_VOCAB_URL:-$(read_dotenv_value CODEGRAPH_EMBEDDING_MODEL_VOCAB_URL)}"
vocab_url="${vocab_url:-https://huggingface.co/nomic-ai/nomic-embed-text-v1.5/resolve/main/vocab.txt?download=true}"

download_file() {
  url="$1"
  destination="$2"
  label="$3"

  if [ -s "$destination" ]; then
    echo "$label already exists at $destination"
    return
  fi

  mkdir -p "$(dirname "$destination")"
  temp_file="${destination}.tmp"
  rm -f "$temp_file"

  echo "Downloading $label to $destination"
  if command -v curl >/dev/null 2>&1; then
    curl -fL --retry 3 --retry-delay 5 --connect-timeout 30 -o "$temp_file" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget --tries=3 --timeout=30 -O "$temp_file" "$url"
  else
    echo "curl or wget is required to download $label" >&2
    exit 1
  fi

  if [ ! -s "$temp_file" ]; then
    echo "Downloaded $label is empty: $temp_file" >&2
    rm -f "$temp_file"
    exit 1
  fi

  mv "$temp_file" "$destination"
}

download_file "$model_url" "$model_dir/model.onnx" "$model_name ONNX model"
download_file "$vocab_url" "$model_dir/vocab.txt" "$model_name vocabulary"

echo "Embedding model files are ready in $model_dir"
