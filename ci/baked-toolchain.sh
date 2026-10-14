#!/usr/bin/env bash
# Report which requested CI toolchains the fleet Linux runner image already
# carries at its manifest pin, so a workflow can skip the step that would
# download them (schoen-lab#3353). This is the repository-local copy of
# schoen-lab's ci/baked-toolchain.sh without its uv mode, whose required
# version lives in the monorepo's runner manifest.
#
# Usage: ci/baked-toolchain.sh TOOL... where each TOOL is one of node,
# python and shellcheck. It writes TOOL=true|false to $GITHUB_OUTPUT for each one. A baked
# node or python is also prepended to $GITHUB_PATH, as actions/setup-node and
# actions/setup-python would do. Outside the fleet image (no image.env, or an
# image built before a tool was baked) every tool reports false and the
# workflow's own setup step runs as before. A probe counts only when its
# command exits zero and prints the pinned version.
set -euo pipefail

image_env=${FLEET_IMAGE_ENV:-/usr/local/share/fleet-runner/image.env}
: "${GITHUB_OUTPUT:?}" "${GITHUB_PATH:?}"

if [[ -f "$image_env" ]]; then
  # shellcheck source=/dev/null
  source "$image_env"
fi

version_output() {
  local output
  output="$("$@" 2> /dev/null)" || return 1
  printf '%s\n' "$output"
}

baked() {
  local version
  case "$1" in
    node)
      local node_bin="/opt/fleet/node-${FLEET_NODE_VERSION:-}/bin"
      [[ -n "${FLEET_NODE_VERSION:-}" ]] \
        && version="$(version_output "$node_bin/node" --version)" \
        && [[ "$version" == "v$FLEET_NODE_VERSION" ]] \
        && printf '%s\n' "$node_bin" >> "$GITHUB_PATH"
      ;;
    python)
      local python_bin="/opt/fleet/python-${FLEET_PYTHON_VERSION:-}/bin"
      [[ -n "${FLEET_PYTHON_VERSION:-}" ]] \
        && version="$(version_output "$python_bin/python" --version)" \
        && [[ "$version" == "Python $FLEET_PYTHON_VERSION" ]] \
        && printf '%s\n' "$python_bin" >> "$GITHUB_PATH"
      ;;
    shellcheck)
      [[ -n "${SHELLCHECK_MAJOR:-}" ]] \
        && version="$(version_output shellcheck --version)" \
        && [[ "$version" == *"version: $SHELLCHECK_MAJOR."* ]]
      ;;
    *)
      printf 'unknown toolchain: %s\n' "$1" >&2
      exit 2
      ;;
  esac
}

if [[ $# -eq 0 ]]; then
  printf 'usage: %s TOOL...\n' "$0" >&2
  exit 2
fi
for tool in "$@"; do
  if baked "$tool"; then
    state=true
  else
    state=false
  fi
  printf '%s=%s\n' "$tool" "$state" >> "$GITHUB_OUTPUT"
  printf 'baked %s: %s\n' "$tool" "$state"
done
