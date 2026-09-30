#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "${SCRIPT_DIR}"

export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:${PATH}"

case "$(uname -m)" in
  arm64)
    DEFAULT_RID="osx-arm64"
    ;;
  x86_64)
    DEFAULT_RID="osx-x64"
    ;;
  *)
    echo "Unsupported Mac architecture: $(uname -m)" >&2
    exit 1
    ;;
esac

RID="${RID:-${DEFAULT_RID}}"

finish() {
  local status="$1"
  echo
  if [[ ${status} -eq 0 ]]; then
    echo "Ansight CLI tools build finished."
    echo "Output: ${SCRIPT_DIR}/products/cli/${RID}/ansight"
  else
    echo "Ansight CLI tools build failed with exit code ${status}."
  fi

  if [[ -t 0 && "${PAUSE_ON_EXIT:-true}" != "false" ]]; then
    read -r -p "Press Return to close this window..." || true
  fi

  exit "${status}"
}
trap 'finish $?' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

echo "Building Ansight CLI tools..."
echo "  Runtime: ${RID}"
echo

RID="${RID}" scripts/package/publish-cli.sh
