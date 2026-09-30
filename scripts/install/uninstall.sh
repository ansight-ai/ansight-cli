#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT="${ANSIGHT_INSTALL_ROOT:-}"
BIN_DIR="${ANSIGHT_BIN_DIR:-}"
DATA_DIR="${ANSIGHT_DATA_DIR:-}"
RECEIPT_PATH="${ANSIGHT_INSTALL_RECEIPT:-}"
CLI_PATH="${ANSIGHT_UNINSTALL_CLI:-}"
PURGE_DATA="ask"
REMOVE_SKILLS="ask"
SIGN_OUT_MODE="remote"
CLEAN_STARTUP=1
CLEAN_PATH=1

usage() {
  cat <<'EOF'
Uninstall the Ansight CLI from macOS or Linux.

Usage:
  uninstall.sh [options]

Options:
  --install-dir <path>       Installer-managed CLI root
  --bin-dir <path>           Directory containing the ansight PATH entry
  --data-dir <path>          CLI working-data root selected for sign-out or purge
  --receipt <path>           Installation receipt to use
  --cli-path <path>          Ansight executable used to stop the host and sign out
  --purge-data               Remove captures, logs, configuration, and local secrets
  --keep-data                Preserve all CLI working data without prompting
  --remove-skills            Remove Ansight skills installed for agent clients
  --keep-skills              Preserve installed agent skills without prompting
  --local-sign-out           Clear local authentication without remote revocation
  --no-sign-out              Leave the current account session intact
  --keep-startup             Leave launchd/systemd/autostart configuration intact
  --keep-path                Leave shell PATH configuration intact
  --help                     Show this help

By default, the uninstaller signs out, stops and removes host startup entries,
and removes only installer-managed CLI files. Interactive sessions are asked
before working data or agent skills are deleted. Non-interactive sessions keep
both unless --purge-data or --remove-skills is supplied explicitly.
EOF
}

fail() {
  echo "Ansight uninstall failed: $*" >&2
  exit 1
}

warn() {
  echo "Warning: $*" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-dir)
      [[ $# -ge 2 ]] || fail "--install-dir requires a value."
      INSTALL_ROOT="$2"
      shift 2
      ;;
    --bin-dir)
      [[ $# -ge 2 ]] || fail "--bin-dir requires a value."
      BIN_DIR="$2"
      shift 2
      ;;
    --data-dir)
      [[ $# -ge 2 ]] || fail "--data-dir requires a value."
      DATA_DIR="$2"
      shift 2
      ;;
    --receipt)
      [[ $# -ge 2 ]] || fail "--receipt requires a value."
      RECEIPT_PATH="$2"
      shift 2
      ;;
    --cli-path)
      [[ $# -ge 2 ]] || fail "--cli-path requires a value."
      CLI_PATH="$2"
      shift 2
      ;;
    --purge-data)
      PURGE_DATA="yes"
      shift
      ;;
    --keep-data)
      PURGE_DATA="no"
      shift
      ;;
    --remove-skills)
      REMOVE_SKILLS="yes"
      shift
      ;;
    --keep-skills)
      REMOVE_SKILLS="no"
      shift
      ;;
    --local-sign-out)
      SIGN_OUT_MODE="local"
      shift
      ;;
    --no-sign-out)
      SIGN_OUT_MODE="none"
      shift
      ;;
    --keep-startup)
      CLEAN_STARTUP=0
      shift
      ;;
    --keep-path)
      CLEAN_PATH=0
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      fail "Unknown option '$1'."
      ;;
  esac
done

case "$(uname -s)" in
  Darwin)
    PLATFORM="macos"
    ;;
  Linux)
    PLATFORM="linux"
    ;;
  *)
    fail "This uninstaller supports macOS and Linux. On Windows, use uninstall.ps1."
    ;;
esac

json_string_property() {
  local property="$1"
  local file="$2"
  sed -n "s/.*\"${property}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "${file}" \
    | head -n 1
}

receipt_owns_install_root() {
  local receipt_path="$1"
  local install_root="$2"
  local receipt_directory_resolved install_root_resolved
  [[ -d "$(dirname "${receipt_path}")" && -d "${install_root}" ]] || return 1
  receipt_directory_resolved="$(cd "$(dirname "${receipt_path}")" && pwd -P)"
  install_root_resolved="$(cd "${install_root}" && pwd -P)"
  [[ "${receipt_directory_resolved}" == "${install_root_resolved}" ]]
}

if [[ -z "${CLI_PATH}" ]]; then
  CLI_PATH="$(command -v ansight 2>/dev/null || true)"
fi
if [[ -z "${RECEIPT_PATH}" && -n "${CLI_PATH}" ]]; then
  VERSION_JSON="$(ANSIGHT_NO_UPDATE_CHECK=1 "${CLI_PATH}" version --json 2>/dev/null || true)"
  RECEIPT_PATH="$(printf '%s\n' "${VERSION_JSON}" \
    | sed -n 's/.*"receiptPath"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    | head -n 1)"
fi

if [[ -z "${INSTALL_ROOT}" && -n "${RECEIPT_PATH}" ]]; then
  INSTALL_ROOT="$(dirname "${RECEIPT_PATH}")"
fi
INSTALL_ROOT="${INSTALL_ROOT:-${HOME}/.local/share/ansight/cli}"
RECEIPT_PATH="${RECEIPT_PATH:-${INSTALL_ROOT}/install.json}"

MANAGED_INSTALL=0
if [[ -f "${RECEIPT_PATH}" ]]; then
  RECEIPT_SCHEMA="$(json_string_property schema "${RECEIPT_PATH}")"
  if [[ "${RECEIPT_SCHEMA}" == "ansight.cli.installation/v1" ]]; then
    RECEIPT_INSTALL_ROOT="$(json_string_property installRoot "${RECEIPT_PATH}")"
    RECEIPT_BIN_DIR="$(json_string_property binDirectory "${RECEIPT_PATH}")"
    if [[ -n "${RECEIPT_INSTALL_ROOT}"
          && "$(basename "${RECEIPT_PATH}")" == "install.json" ]] \
        && receipt_owns_install_root "${RECEIPT_PATH}" "${RECEIPT_INSTALL_ROOT}"; then
      MANAGED_INSTALL=1
      INSTALL_ROOT="${RECEIPT_INSTALL_ROOT}"
      if [[ -z "${BIN_DIR}" && -n "${RECEIPT_BIN_DIR}" ]]; then
        BIN_DIR="${RECEIPT_BIN_DIR}"
      fi
    else
      warn "The receipt installRoot does not own ${RECEIPT_PATH}; refusing managed file removal."
    fi
  fi
fi
BIN_DIR="${BIN_DIR:-${HOME}/.local/bin}"

if [[ -z "${DATA_DIR}" ]]; then
  if [[ "${PLATFORM}" == "macos" ]]; then
    DATA_DIR="${HOME}/Library/Application Support/Ansight/Cli"
  elif [[ -n "${XDG_STATE_HOME:-}" ]]; then
    DATA_DIR="${XDG_STATE_HOME%/}/ansight"
  else
    DATA_DIR="${HOME}/.local/state/ansight"
  fi
fi

if [[ -z "${CLI_PATH}" && -x "${BIN_DIR}/ansight" ]]; then
  CLI_PATH="${BIN_DIR}/ansight"
fi

prompt_yes_no() {
  local prompt="$1"
  local reply
  if [[ ! -r /dev/tty ]]; then
    return 1
  fi

  while true; do
    printf '%s [y/N]: ' "${prompt}" >/dev/tty
    IFS= read -r reply </dev/tty || return 1
    case "${reply}" in
      y|Y|yes|YES|Yes)
        return 0
        ;;
      n|N|no|NO|No|'')
        return 1
        ;;
      *)
        echo "Please enter y or n." >/dev/tty
        ;;
    esac
  done
}

validate_removal_target() {
  local path="$1"
  local label="$2"
  local resolved_path
  [[ -n "${path}" ]] || fail "${label} path is empty."
  if [[ -d "${path}" ]]; then
    resolved_path="$(cd "${path}" && pwd -P)"
  else
    resolved_path="$(cd "$(dirname "${path}")" 2>/dev/null && pwd -P)/$(basename "${path}")" \
      || fail "Unable to resolve ${label} path: ${path}"
  fi
  case "${resolved_path}" in
    /|"${HOME}"|"${PWD}")
      fail "Refusing to remove unsafe ${label} path: ${resolved_path}"
      ;;
  esac
}

if [[ -n "${CLI_PATH}" && -x "${CLI_PATH}" ]]; then
  echo "Stopping the Ansight host..."
  ANSIGHT_NO_UPDATE_CHECK=1 "${CLI_PATH}" host stop --data-dir "${DATA_DIR}" >/dev/null 2>&1 || true

  case "${SIGN_OUT_MODE}" in
    remote)
      echo "Signing out of Ansight..."
      if ! ANSIGHT_NO_UPDATE_CHECK=1 "${CLI_PATH}" auth logout --data-dir "${DATA_DIR}"; then
        warn "Remote revocation was unavailable; clearing the local account session."
        ANSIGHT_NO_UPDATE_CHECK=1 "${CLI_PATH}" auth logout --local --data-dir "${DATA_DIR}" \
          || warn "The local account session could not be cleared."
      fi
      ;;
    local)
      echo "Clearing the local Ansight account session..."
      ANSIGHT_NO_UPDATE_CHECK=1 "${CLI_PATH}" auth logout --local --data-dir "${DATA_DIR}" \
        || warn "The local account session could not be cleared."
      ;;
  esac
elif [[ "${SIGN_OUT_MODE}" != "none" ]]; then
  warn "The Ansight CLI was not found, so account sign-out could not be performed."
fi

if [[ "${CLEAN_STARTUP}" -eq 1 ]]; then
  if [[ "${PLATFORM}" == "macos" ]]; then
    PLIST_PATH="${HOME}/Library/LaunchAgents/ai.ansight.host.plist"
    launchctl bootout "gui/$(id -u)" "${PLIST_PATH}" >/dev/null 2>&1 || true
    if [[ -f "${PLIST_PATH}" ]]; then
      rm -f "${PLIST_PATH}"
      echo "Removed the Ansight launchd login item."
    fi
  else
    SYSTEMD_UNIT="${HOME}/.config/systemd/user/ansight-host.service"
    if command -v systemctl >/dev/null 2>&1; then
      systemctl --user disable --now ansight-host.service >/dev/null 2>&1 || true
    fi
    if [[ -f "${SYSTEMD_UNIT}" ]]; then
      rm -f "${SYSTEMD_UNIT}"
      if command -v systemctl >/dev/null 2>&1; then
        systemctl --user daemon-reload >/dev/null 2>&1 || true
      fi
      echo "Removed the Ansight systemd user service."
    fi

    AUTOSTART_ENTRY="${HOME}/.config/autostart/ansight-host.desktop"
    if [[ -f "${AUTOSTART_ENTRY}" ]]; then
      rm -f "${AUTOSTART_ENTRY}"
      echo "Removed the Ansight desktop login item."
    fi
  fi
fi

remove_shell_path_entry() {
  local rc_file="$1"
  local path_line="export PATH=\"${BIN_DIR}:\$PATH\""
  local pending_file
  [[ -f "${rc_file}" ]] || return
  pending_file="${rc_file}.ansight-uninstall-$$"
  cp -p "${rc_file}" "${pending_file}"
  awk -v path_line="${path_line}" '
    $0 == "# Added by the Ansight CLI installer" { pending_comment = $0; next }
    $0 == path_line {
      pending_comment = ""
      next
    }
    pending_comment != "" {
      print pending_comment
      pending_comment = ""
    }
    { print }
    END {
      if (pending_comment != "") print pending_comment
    }
  ' "${rc_file}" > "${pending_file}"
  if cmp -s "${rc_file}" "${pending_file}"; then
    rm -f "${pending_file}"
  else
    mv "${pending_file}" "${rc_file}"
    echo "Removed ${BIN_DIR} from ${rc_file}."
  fi
}

if [[ "${MANAGED_INSTALL}" -eq 1 ]]; then
  validate_removal_target "${INSTALL_ROOT}" "installation"
  if [[ "${CLEAN_PATH}" -eq 1 ]]; then
    remove_shell_path_entry "${HOME}/.zprofile"
    remove_shell_path_entry "${HOME}/.bash_profile"
    remove_shell_path_entry "${HOME}/.bashrc"
  fi

  BIN_LINK="${BIN_DIR}/ansight"
  if [[ -L "${BIN_LINK}" ]]; then
    rm -f "${BIN_LINK}"
  elif [[ -e "${BIN_LINK}" ]]; then
    warn "Preserving ${BIN_LINK} because it is not an installer-managed symbolic link."
  fi

  for artifact in versions current install.json update-check.json; do
    artifact_path="${INSTALL_ROOT}/${artifact}"
    if [[ -L "${artifact_path}" || -f "${artifact_path}" ]]; then
      rm -f "${artifact_path}"
    elif [[ -d "${artifact_path}" ]]; then
      rm -rf "${artifact_path}"
    fi
  done
  rmdir "${INSTALL_ROOT}" >/dev/null 2>&1 || true
  rmdir "${BIN_DIR}" >/dev/null 2>&1 || true
  echo "Removed the installer-managed Ansight CLI files."
else
  warn "No valid Ansight installation receipt was found at ${RECEIPT_PATH}; no binary files were deleted."
fi

if [[ "${REMOVE_SKILLS}" == "ask" ]]; then
  if prompt_yes_no "Remove the installed Ansight agent skills, including any local edits to them?"; then
    REMOVE_SKILLS="yes"
  else
    REMOVE_SKILLS="no"
  fi
fi
if [[ "${REMOVE_SKILLS}" == "yes" ]]; then
  for skills_root in "${HOME}/.agents/skills" "${HOME}/.cursor/skills" "${HOME}/.claude/skills"; do
    for skill in \
      ansight-cli-setup \
      ansight-app-inspection \
      ansight-operate-live-app \
      ansight-use-remote-app-tools \
      ansight-investigate-session \
      ansight-annotate-session \
      ansight-assess-automation-readiness \
      ansight-ui-testing; do
      skill_path="${skills_root}/${skill}"
      if [[ -d "${skill_path}" ]]; then
        rm -rf "${skill_path}"
        echo "Removed agent skill: ${skill_path}"
      fi
    done
  done
fi

if [[ "${PURGE_DATA}" == "ask" ]]; then
  if prompt_yes_no "Permanently remove all Ansight CLI captures, logs, and configuration at ${DATA_DIR}?"; then
    PURGE_DATA="yes"
  else
    PURGE_DATA="no"
  fi
fi
if [[ "${PURGE_DATA}" == "yes" ]]; then
  validate_removal_target "${DATA_DIR}" "working-data"
  if [[ -d "${DATA_DIR}" ]]; then
    rm -rf "${DATA_DIR}"
    echo "Removed Ansight CLI working data: ${DATA_DIR}"
  fi
else
  echo "Preserved Ansight CLI working data: ${DATA_DIR}"
fi

echo "Ansight CLI uninstall complete."
