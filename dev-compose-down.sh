#!/usr/bin/env bash
# Insight platform — docker-compose dev stack tear-down.
#
# Stops all containers but PRESERVES data volumes (mariadb-data,
# clickhouse-data, redis-data, redpanda-data). Add --volumes to wipe
# everything, including build artefacts.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

ENV_FILE=".env.compose"
WIPE=false

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env-file=*) ENV_FILE="${1#*=}"; shift ;;
    --env-file)   ENV_FILE="$2"; shift 2 ;;
    --volumes|-v) WIPE=true; shift ;;
    -h|--help)
      echo "usage: $0 [--env-file=PATH] [--volumes]"
      exit 0 ;;
    *) echo "ERROR: unknown arg: $1" >&2; exit 2 ;;
  esac
done

[[ -f "$ENV_FILE" ]] || ENV_FILE=".env.compose.example"
OVERRIDE="compose/override.generated.yml"
[[ -f "$OVERRIDE" ]] || OVERRIDE="/dev/null"

COMPOSE=(docker compose --env-file "$ENV_FILE" -f docker-compose.yml)
[[ "$OVERRIDE" != "/dev/null" ]] && COMPOSE+=(-f "$OVERRIDE")

# All frontend profiles passed so the active one (whichever) is included
# in the stop.
"${COMPOSE[@]}" \
  --profile front-dev --profile front-built --profile front-ghcr \
  --profile build \
  down $([[ "$WIPE" == "true" ]] && echo "--volumes --remove-orphans")

if [[ "$WIPE" == "true" ]]; then
  echo "Wiping host-side build artefacts (compose/build/)..."
  rm -rf compose/build/
fi

echo "Done."
