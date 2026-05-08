#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Airbyte Toolkit — Host-side prerequisites
#
# Two host-side prerequisites that init.sh / register.sh / connect.sh need
# whenever they're invoked from outside the cluster:
#
#   1. CLI tooling: `yq`, `jq`, and Python `yaml` (PyYAML). The toolkit
#      shells out to yq/jq for descriptor parsing and uses inline `python3 -
#      <<PY ... import yaml ...` blocks for state-file IO. Missing tools
#      surface as `command not found` / `ModuleNotFoundError` deep inside
#      register.sh and have no actionable hint.
#
#   2. Port-forward to airbyte-server. When AIRBYTE_API is unset and we're
#      not running in-cluster, env.sh defaults to http://localhost:8001 and
#      will fail workspace resolution unless `kubectl port-forward
#      svc/airbyte-airbyte-server-svc 8001:8001` is already open. Asking
#      the operator to remember this is a footgun; the toolkit can open
#      the forward itself and tear it down on exit.
#
# Both functions are no-ops when running in-cluster (detected via the
# ServiceAccount token file). Both are idempotent — re-sourcing or re-
# calling them when state is already correct is safe (init.sh opens the
# port-forward, sub-scripts re-assert it and find it already up).
# ---------------------------------------------------------------------------

# Standard install dir for downloaded binaries — out of the way of system
# package managers, doesn't require root, and survives across runs.
INSIGHT_BIN_DIR="${INSIGHT_BIN_DIR:-$HOME/.insight/bin}"

_in_cluster() {
  [[ -f /var/run/secrets/kubernetes.io/serviceaccount/token ]]
}

# ---------------------------------------------------------------------------
# Detect host platform → returns "<os>/<arch>" pair for the OS/arch that
# yq and jq release artifacts are published under. Stays consistent across
# both projects' naming conventions in the download_* helpers below.
# ---------------------------------------------------------------------------
_detect_platform() {
  local os arch
  case "$(uname -s)" in
    Linux*)   os=linux ;;
    Darwin*)  os=darwin ;;
    MINGW*|MSYS*|CYGWIN*) os=windows ;;
    *) echo "ERROR: unsupported OS: $(uname -s)" >&2; return 1 ;;
  esac
  case "$(uname -m)" in
    x86_64|amd64) arch=amd64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) echo "ERROR: unsupported arch: $(uname -m)" >&2; return 1 ;;
  esac
  echo "$os/$arch"
}

_download() {
  local url="$1" dest="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -sSLf -o "$dest" "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$dest" "$url"
  else
    echo "ERROR: neither curl nor wget available — install one and retry" >&2
    return 1
  fi
}

_install_yq() {
  local platform os arch ext url
  platform=$(_detect_platform) || return 1
  os="${platform%/*}"; arch="${platform#*/}"
  ext=""
  [[ "$os" == "windows" ]] && ext=".exe"
  url="https://github.com/mikefarah/yq/releases/latest/download/yq_${os}_${arch}${ext}"
  echo "  Installing yq from $url" >&2
  mkdir -p "$INSIGHT_BIN_DIR"
  _download "$url" "$INSIGHT_BIN_DIR/yq${ext}" || return 1
  chmod +x "$INSIGHT_BIN_DIR/yq${ext}"
}

_install_jq() {
  local platform os arch ext name url
  platform=$(_detect_platform) || return 1
  os="${platform%/*}"; arch="${platform#*/}"
  ext=""
  # jq release naming: linux→linux-amd64, darwin→macos-amd64,
  # windows→windows-amd64.exe. The OS segment differs from yq's.
  case "$os" in
    linux)   name="jq-linux-${arch}" ;;
    darwin)  name="jq-macos-${arch}" ;;
    windows) name="jq-windows-${arch}.exe"; ext=".exe" ;;
  esac
  url="https://github.com/jqlang/jq/releases/latest/download/${name}"
  echo "  Installing jq from $url" >&2
  mkdir -p "$INSIGHT_BIN_DIR"
  _download "$url" "$INSIGHT_BIN_DIR/jq${ext}" || return 1
  chmod +x "$INSIGHT_BIN_DIR/jq${ext}"
}

_install_pyyaml() {
  echo "  Installing PyYAML via pip" >&2
  # --user keeps the install out of the system site-packages; works with
  # the venv-less interpreter on most operator workstations. Fall back to
  # plain pip install if --user is rejected (some Pythons forbid it).
  python3 -m pip install --user --quiet pyyaml \
    || python3 -m pip install --quiet pyyaml
}

# ---------------------------------------------------------------------------
# ensure_tooling — verifies yq, jq, and PyYAML are usable; silently
# installs whatever is missing into $INSIGHT_BIN_DIR. Idempotent.
# ---------------------------------------------------------------------------
ensure_tooling() {
  _in_cluster && return 0

  # If we previously installed binaries here, prepend on every call so
  # subsequent commands in this shell can see them (we may not have
  # been the shell that installed them).
  if [[ -d "$INSIGHT_BIN_DIR" ]]; then
    case ":$PATH:" in
      *":$INSIGHT_BIN_DIR:"*) ;;
      *) export PATH="$INSIGHT_BIN_DIR:$PATH" ;;
    esac
  fi

  if ! command -v yq >/dev/null 2>&1; then
    _install_yq || return 1
    export PATH="$INSIGHT_BIN_DIR:$PATH"
  fi
  if ! command -v jq >/dev/null 2>&1; then
    _install_jq || return 1
    export PATH="$INSIGHT_BIN_DIR:$PATH"
  fi
  if ! python3 -c "import yaml" >/dev/null 2>&1; then
    _install_pyyaml || return 1
  fi
}

# ---------------------------------------------------------------------------
# ensure_airbyte_pf — opens a background `kubectl port-forward` to
# airbyte-server when running from host and 8001 isn't already serving
# the API. Registers an EXIT trap so the forward dies with the script.
#
# Honors:
#   - AIRBYTE_API: if explicitly set to a non-localhost URL, we trust the
#     operator and skip. (Set to http://localhost:8001 deliberately to
#     opt INTO managed PF for that exact endpoint.)
#   - INSIGHT_NAMESPACE: namespace the airbyte-server svc lives in.
# ---------------------------------------------------------------------------
ensure_airbyte_pf() {
  _in_cluster && return 0

  # If the caller pinned AIRBYTE_API to something other than localhost:8001,
  # they've already taken responsibility for reachability. Skip.
  if [[ -n "${AIRBYTE_API:-}" && "${AIRBYTE_API}" != "http://localhost:8001" ]]; then
    return 0
  fi

  local ns="${INSIGHT_NAMESPACE:-insight}"
  local api="http://localhost:8001"

  # Already responsive? Use whatever's on the other end (PF or local airbyte).
  if curl -sf -o /dev/null --max-time 2 "${api}/api/v1/health" 2>/dev/null; then
    return 0
  fi

  echo "  Opening port-forward to svc/airbyte-airbyte-server-svc 8001:8001" >&2
  kubectl -n "$ns" port-forward svc/airbyte-airbyte-server-svc 8001:8001 \
    >/dev/null 2>&1 &
  local pf_pid=$!
  # Trap EXIT (covers normal exit, set -e abort, signal). Use a defensive
  # kill — process may have already died if PF errored out.
  # shellcheck disable=SC2064
  trap "kill $pf_pid 2>/dev/null || true" EXIT

  # Wait for the forward to become ready. 30s ceiling avoids hangs when
  # the service is missing or the API server is itself unhealthy.
  local i
  for i in $(seq 1 30); do
    if curl -sf -o /dev/null --max-time 2 "${api}/api/v1/health" 2>/dev/null; then
      return 0
    fi
    # If the PF process died, fail loudly instead of looping.
    if ! kill -0 "$pf_pid" 2>/dev/null; then
      echo "ERROR: port-forward to airbyte-server died unexpectedly" >&2
      return 1
    fi
    sleep 1
  done
  echo "ERROR: airbyte-server did not become reachable on $api within 30s" >&2
  return 1
}
