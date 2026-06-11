#!/usr/bin/env bash
# Insight platform — rebuild a single service's artefact and let the
# already-running container pick it up via ENABLE_AUTO_RELOAD.
#
# Usage:
#   ./dev-compose-build.sh api-gateway          # rebuild Rust gateway only
#   ./dev-compose-build.sh analytics-api
#   ./dev-compose-build.sh identity             # rebuild .NET identity
#   ./dev-compose-build.sh frontend             # rebuild pnpm dist
#   ./dev-compose-build.sh rust                 # both Rust services
#   ./dev-compose-build.sh all                  # everything
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

ENV_FILE=".env.compose"
[[ -f "$ENV_FILE" ]] || ENV_FILE=".env.compose.example"
set -a; source "$ENV_FILE"; set +a

TARGET="${1:-}"
[[ -z "$TARGET" ]] && { echo "usage: $0 <api-gateway|analytics-api|identity|frontend|rust|all>"; exit 2; }

COMPOSE=(docker compose --env-file "$ENV_FILE" -f docker-compose.yml --profile build)

build_rust_bins() {
  local bins=("$@")
  "${COMPOSE[@]}" run --rm build-rust bash -c "
    set -eux
    apt-get update && apt-get install -y --no-install-recommends \
      protobuf-compiler libprotobuf-dev pkg-config libssl-dev > /dev/null
    cargo build --release $(printf -- '--bin %s ' "${bins[@]}")
    mkdir -p /out/api-gateway /out/analytics-api
    [ -f /target/release/insight-api-gateway ] && install -m 0755 /target/release/insight-api-gateway /out/api-gateway/insight-api-gateway || true
    [ -f /target/release/analytics-api ]       && install -m 0755 /target/release/analytics-api       /out/analytics-api/analytics-api || true
  "
}

case "$TARGET" in
  api-gateway)   build_rust_bins insight-api-gateway ;;
  analytics-api) build_rust_bins analytics-api ;;
  rust)          build_rust_bins insight-api-gateway analytics-api ;;
  identity)      "${COMPOSE[@]}" run --rm build-dotnet ;;
  frontend)      "${COMPOSE[@]}" run --rm build-frontend ;;
  all)
    build_rust_bins insight-api-gateway analytics-api
    "${COMPOSE[@]}" run --rm build-dotnet
    "${COMPOSE[@]}" run --rm build-frontend
    ;;
  *)
    echo "ERROR: unknown target: $TARGET" >&2
    exit 2 ;;
esac

echo "Done. If a runtime container is up with ENABLE_AUTO_RELOAD=true it will restart automatically."
