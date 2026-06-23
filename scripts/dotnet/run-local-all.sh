#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="${DOTNET_RAG_ENV_FILE:-$ROOT_DIR/deploy/compose/dotnet-local.env}"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "env file not found: $ENV_FILE" >&2
  exit 1
fi

set -a
source "$ENV_FILE"
set +a

RAG_PORT="${RAG_PORT:-8081}"
INGESTOR_PORT="${INGESTOR_PORT:-8082}"
RERANKER_PORT="${RERANKER_PORT:-8083}"

pids=()
cleanup() {
  for pid in "${pids[@]:-}"; do
    kill "$pid" 2>/dev/null || true
  done
}
trap cleanup EXIT INT TERM

(
  cd "$ROOT_DIR"
  ASPNETCORE_URLS="http://0.0.0.0:${RAG_PORT}" dotnet run --project src/dotnet_rag/rag_server/DotnetRag.Rag_Server.csproj
) &
pids+=($!)

(
  cd "$ROOT_DIR"
  ASPNETCORE_URLS="http://0.0.0.0:${INGESTOR_PORT}" dotnet run --project src/dotnet_rag/ingestor_server/DotnetRag.Ingestor_Server.csproj
) &
pids+=($!)

(
  cd "$ROOT_DIR"
  ASPNETCORE_URLS="http://0.0.0.0:${RERANKER_PORT}" dotnet run --project src/dotnet_rag/reranker_service/DotnetRag.Reranker_Service.csproj
) &
pids+=($!)

wait
