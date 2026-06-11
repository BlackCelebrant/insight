#!/usr/bin/env bash
# Insight platform — docker-compose dev stack up.
#
# Builds the local Linux binaries (Rust + .NET) and frontend dist as
# needed, generates a per-run override for any service the operator
# wants pulled from ghcr, then `docker compose up -d`.
#
# Usage:
#   ./dev-compose-up.sh                                 # everything local
#   ./dev-compose-up.sh --from-ghcr=identity            # pull identity, build the rest
#   ./dev-compose-up.sh --build-only=api-gateway        # only build api-gateway; pull the others
#   ./dev-compose-up.sh --no-frontend                   # skip frontend variant
#   ./dev-compose-up.sh --frontend-mode=built           # override FRONTEND_MODE
#   ./dev-compose-up.sh --skip-build                    # don't rebuild artefacts; reuse existing
#   ./dev-compose-up.sh --env-file=.env.compose.work    # alternate dotenv
#
# Out-of-scope:
#   --start-airbyte / --start-argo
#       Airbyte + Argo Workflows are NOT shipped by this stack — see
#       CONTRIBUTING.md. The script prints a pointer when these flags
#       are passed.

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

# ── Argument parsing ──────────────────────────────────────────────────
ENV_FILE=".env.compose"
FROM_GHCR_CSV=""
BUILD_ONLY_CSV=""
FRONTEND_MODE_OVERRIDE=""
SKIP_BUILD=false
NO_FRONTEND=false

usage() {
  grep -E '^# ?' "$0" | sed 's/^# \{0,1\}//' | head -40
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env-file=*)        ENV_FILE="${1#*=}"; shift ;;
    --env-file)          ENV_FILE="$2"; shift 2 ;;
    --from-ghcr=*)       FROM_GHCR_CSV="${1#*=}"; shift ;;
    --from-ghcr)         FROM_GHCR_CSV="$2"; shift 2 ;;
    --build-only=*)      BUILD_ONLY_CSV="${1#*=}"; shift ;;
    --build-only)        BUILD_ONLY_CSV="$2"; shift 2 ;;
    --frontend-mode=*)   FRONTEND_MODE_OVERRIDE="${1#*=}"; shift ;;
    --frontend-mode)     FRONTEND_MODE_OVERRIDE="$2"; shift 2 ;;
    --skip-build)        SKIP_BUILD=true; shift ;;
    --no-frontend)       NO_FRONTEND=true; shift ;;
    --start-airbyte|--start-argo)
      echo "ERROR: ${1} is not supported by the compose stack." >&2
      echo "       Airbyte + Argo Workflows need k8s. Install orbstack/k3d/kind" >&2
      echo "       and use the existing ./dev-up.sh path. See CONTRIBUTING.md." >&2
      exit 2 ;;
    -h|--help)           usage ;;
    *)                   echo "ERROR: unknown arg: $1" >&2; usage ;;
  esac
done

# ── Load env file ─────────────────────────────────────────────────────
if [[ ! -f "$ENV_FILE" ]]; then
  echo "ERROR: env file not found: $ENV_FILE" >&2
  echo "       Run:  cp .env.compose.example .env.compose" >&2
  exit 1
fi
set -a; source "$ENV_FILE"; set +a

# CLI overrides win over dotenv.
[[ -n "$FRONTEND_MODE_OVERRIDE" ]] && FRONTEND_MODE="$FRONTEND_MODE_OVERRIDE"
FRONTEND_MODE="${FRONTEND_MODE:-dev}"

# ── Resolve which services go to ghcr ─────────────────────────────────
# Three signals can mark a service as ghcr-sourced:
#   1. `${SVC}_IMAGE` set in .env.compose to a ghcr ref
#   2. --from-ghcr=svc1,svc2 on CLI
#   3. --build-only=svcN  (everything NOT in this list goes ghcr)
#
# Stored as plain space-separated strings so the script runs on bash 3.2
# (Mac default shell) which lacks associative arrays.
ALL_BACKEND="api-gateway analytics-api identity"
GHCR_LIST=""
BUILD_LIST=""

trim()    { local s="$1"; s="${s#"${s%%[![:space:]]*}"}"; s="${s%"${s##*[![:space:]]}"}"; printf '%s' "$s"; }
contains() { case " $1 " in *" $2 "*) return 0 ;; esac; return 1; }
add()     { local list="$1" item="$2"; contains "$list" "$item" && printf '%s' "$list" || printf '%s %s' "$list" "$item"; }

# Seed from env image vars.
[[ -n "${API_GATEWAY_IMAGE:-}"   ]] && GHCR_LIST=$(add "$GHCR_LIST" api-gateway)
[[ -n "${ANALYTICS_API_IMAGE:-}" ]] && GHCR_LIST=$(add "$GHCR_LIST" analytics-api)
[[ -n "${IDENTITY_IMAGE:-}"      ]] && GHCR_LIST=$(add "$GHCR_LIST" identity)

if [[ -n "$FROM_GHCR_CSV" ]]; then
  OLD_IFS=$IFS; IFS=','
  for s in $FROM_GHCR_CSV; do GHCR_LIST=$(add "$GHCR_LIST" "$(trim "$s")"); done
  IFS=$OLD_IFS
fi

if [[ -n "$BUILD_ONLY_CSV" ]]; then
  OLD_IFS=$IFS; IFS=','
  for s in $BUILD_ONLY_CSV; do BUILD_LIST=$(add "$BUILD_LIST" "$(trim "$s")"); done
  IFS=$OLD_IFS
  for s in $ALL_BACKEND; do
    contains "$BUILD_LIST" "$s" || GHCR_LIST=$(add "$GHCR_LIST" "$s")
  done
fi

# Apply default ghcr image names for services flagged ghcr but without
# an explicit `${SVC}_IMAGE` set.
contains "$GHCR_LIST" api-gateway   && [[ -z "${API_GATEWAY_IMAGE:-}"   ]] && export API_GATEWAY_IMAGE="ghcr.io/constructorfabric/insight-api-gateway:${API_GATEWAY_GHCR_TAG:-latest}"
contains "$GHCR_LIST" analytics-api && [[ -z "${ANALYTICS_API_IMAGE:-}" ]] && export ANALYTICS_API_IMAGE="ghcr.io/constructorfabric/insight-analytics-api:${ANALYTICS_API_GHCR_TAG:-latest}"
contains "$GHCR_LIST" identity      && [[ -z "${IDENTITY_IMAGE:-}"      ]] && export IDENTITY_IMAGE="ghcr.io/constructorfabric/insight-identity:${IDENTITY_GHCR_TAG:-latest}"
true  # don't let the last `contains` failing cause set -e to abort the script

# ── Generate per-run override ─────────────────────────────────────────
# For each service in ghcr mode: drop the build directive and remove the
# bind-mount that would shadow the image's baked binary, and let the
# container use its own ENTRYPOINT/CMD (we override with !reset null so
# compose strips the shared-entrypoint command).
OVERRIDE="compose/override.generated.yml"
mkdir -p compose
{
  echo "# Auto-generated by dev-compose-up.sh — DO NOT EDIT BY HAND."
  echo "# Per-run override that flips selected services to ghcr mode."
  # `services: {}` is valid YAML and lets compose merge a no-op file.
  if [[ -z "$GHCR_LIST" ]]; then
    echo "services: {}"
  else
    echo "services:"
    for svc in $ALL_BACKEND; do
      if contains "$GHCR_LIST" "$svc"; then
        cat <<YML
  ${svc}:
    build: !reset null
    volumes: !override []
    entrypoint: !reset null
    command: !reset null
YML
      fi
    done
  fi
} > "$OVERRIDE"

# ── Compose invocation ────────────────────────────────────────────────
COMPOSE=(docker compose --env-file "$ENV_FILE" -f docker-compose.yml -f "$OVERRIDE")

# Frontend profile selection.
PROFILES=()
if [[ "$NO_FRONTEND" != "true" ]]; then
  case "$FRONTEND_MODE" in
    dev|built|ghcr) PROFILES+=(--profile "front-$FRONTEND_MODE") ;;
    *) echo "ERROR: FRONTEND_MODE must be one of: dev built ghcr (got: $FRONTEND_MODE)" >&2; exit 1 ;;
  esac
fi

# ── Build phase ───────────────────────────────────────────────────────
# Produce Linux binaries for every service NOT flagged ghcr. Builders
# run as compose services with profile=build; we invoke them with `run`.
if [[ "$SKIP_BUILD" != "true" ]]; then
  echo "=== Building artefacts (skip with --skip-build) ==="

  # Rust services share one builder invocation.
  RUST_BINS=""
  contains "$GHCR_LIST" api-gateway   || RUST_BINS="$RUST_BINS insight-api-gateway"
  contains "$GHCR_LIST" analytics-api || RUST_BINS="$RUST_BINS analytics-api"
  RUST_BINS=$(trim "$RUST_BINS")
  if [[ -n "$RUST_BINS" ]]; then
    echo "--- Rust:$RUST_BINS"
    RUST_BIN_FLAGS=""
    for b in $RUST_BINS; do RUST_BIN_FLAGS="$RUST_BIN_FLAGS --bin $b"; done
    "${COMPOSE[@]}" --profile build run --rm \
      build-rust bash -c "
        set -eux
        apt-get update && apt-get install -y --no-install-recommends \
          protobuf-compiler libprotobuf-dev pkg-config libssl-dev > /dev/null
        cargo build --release$RUST_BIN_FLAGS
        mkdir -p /out/api-gateway /out/analytics-api
        [ -f /target/release/insight-api-gateway ] && \
          install -m 0755 /target/release/insight-api-gateway /out/api-gateway/insight-api-gateway || true
        [ -f /target/release/analytics-api ] && \
          install -m 0755 /target/release/analytics-api      /out/analytics-api/analytics-api || true
      "
  fi

  if ! contains "$GHCR_LIST" identity; then
    echo "--- .NET: identity"
    "${COMPOSE[@]}" --profile build run --rm build-dotnet
  fi

  if [[ "$NO_FRONTEND" != "true" && "$FRONTEND_MODE" == "built" ]]; then
    echo "--- Frontend: pnpm build"
    "${COMPOSE[@]}" --profile build run --rm build-frontend
  fi
fi

# Bootstrap empty bind-mount targets for services that ARE ghcr-sourced
# so docker doesn't error on a missing host path. (No-op in practice
# because the override drops the bind-mount, but keeps the layout sane.)
for svc in $ALL_BACKEND; do
  if contains "$GHCR_LIST" "$svc"; then
    mkdir -p "compose/build/$svc"
  fi
done

# ── Up ────────────────────────────────────────────────────────────────
echo "=== docker compose up ==="
# `${arr[@]+...}` keeps set -u happy with empty arrays on bash 3.2 (Mac).
"${COMPOSE[@]}" ${PROFILES[@]+"${PROFILES[@]}"} up -d --remove-orphans

# Brief status.
echo
"${COMPOSE[@]}" ps
echo
echo "Bring down: ./dev-compose-down.sh"
echo "Rebuild one: ./dev-compose-build.sh <api-gateway|analytics-api|identity|frontend>"
