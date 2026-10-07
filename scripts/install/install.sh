#!/usr/bin/env bash
set -euo pipefail

PUBLIC_RELEASE_URL="https://www.ansight.ai/release.json"
PREVIEW_RELEASE_URL="https://www.ansight.ai/preview/release.json"
PUBLIC_DOWNLOAD_BASE_URL="https://ansightaus.blob.core.windows.net/builds/cli"
PREVIEW_DOWNLOAD_BASE_URL="https://ansightaus.blob.core.windows.net/builds/cli/preview"
CHANNEL="${ANSIGHT_INSTALL_CHANNEL:-}"
RELEASE_URL="${ANSIGHT_RELEASE_URL:-}"
DOWNLOAD_BASE_URL="${ANSIGHT_DOWNLOAD_BASE_URL:-}"
PORTAL_URL="${ANSIGHT_PORTAL_URL:-https://app.ansight.ai/?source=cli}"
if [[ "${ANSIGHT_ACQUISITION_ID:-}" =~ ^[0-9a-fA-F-]{36}$ ]]; then
  if [[ "${PORTAL_URL}" == *\?* ]]; then PORTAL_URL="${PORTAL_URL}&journey_id=${ANSIGHT_ACQUISITION_ID}"; else PORTAL_URL="${PORTAL_URL}?journey_id=${ANSIGHT_ACQUISITION_ID}"; fi
fi
VERSION="${ANSIGHT_INSTALL_VERSION:-}"
BUILD_NUMBER="${ANSIGHT_INSTALL_BUILD_NUMBER:-}"
INSTALL_ROOT="${ANSIGHT_INSTALL_ROOT:-${HOME}/.local/share/ansight/cli}"
BIN_DIR="${ANSIGHT_BIN_DIR:-${HOME}/.local/bin}"
ASSUME_YES=0
SKIP_SETUP=0
SECRET_KEY_FILE="${ANSIGHT_SECRET_KEY_FILE:-}"
ANDROID_TARGET="${ANSIGHT_INSTALL_ANDROID_TARGET:-}"
SETUP_ANDROID=0
ACCEPT_ANDROID_LICENSES=0
CREDENTIALS_READY=0
CORE_READY=0
ANDROID_READY="not tested (select --android-target android-device or android-emulator)"
UPDATE_PATH=1
PROMPT_UNAVAILABLE_REPORTED=0
SKILL_INSTALL_NOTICE_SHOWN=0
EXPECTED_MACOS_SIGNING_AUTHORITY="Developer ID Application: Ansight, Inc. (P34C7MACUW)"
EXPECTED_MACOS_TEAM_ID="P34C7MACUW"

usage() {
  cat <<'EOF'
Install the Ansight CLI for macOS or Linux.

Usage:
  install.sh [options]

Options:
  --version <version>           Install a specific version instead of the public release
  --build-number <number>       Install a specific YYYYMMDDNN integer build
  --channel <public|preview>    Select the update channel (default: public)
  --install-dir <path>          Versioned CLI root (default: ~/.local/share/ansight/cli)
  --bin-dir <path>              Directory for the ansight PATH entry (default: ~/.local/bin)
  --release-url <url>           Public release manifest URL
  --download-base-url <url>     CLI archive base URL
  --yes                         Answer yes to every setup prompt
  --no-setup                    Install only; report readiness but skip setup and allow missing prerequisites
  --secret-key-file <path>       Validate and save a persistent external credential key reference
  --android-target <target>     Verify android-device or android-emulator prerequisites
  --setup-android               Optionally install missing Android tools (defaults to android-device)
  --accept-android-licenses     Explicitly accept Android SDK licenses; --yes does not accept them
  --no-path-update              Do not update a shell startup file when --bin-dir is absent from PATH
  --help                        Show this help

Environment variables with matching ANSIGHT_* names may also set these values.
EOF
}

fail() {
  echo "Ansight install failed: $*" >&2
  exit 1
}

verify_macos_cli_signature() {
  local executable="$1"
  local signature_details
  if [[ "${PLATFORM:-}" != "osx" ]]; then
    return
  fi

  [[ -x /usr/bin/codesign ]] || fail "codesign is required to verify the macOS CLI."
  /usr/bin/codesign --verify --strict --verbose=2 "${executable}" \
    || fail "The macOS CLI has an invalid code signature."
  signature_details="$(/usr/bin/codesign -dv --verbose=4 "${executable}" 2>&1)" \
    || fail "The macOS CLI signature could not be inspected."
  /usr/bin/grep -Fq "Authority=${EXPECTED_MACOS_SIGNING_AUTHORITY}" <<<"${signature_details}" \
    || fail "The macOS CLI is not signed by ${EXPECTED_MACOS_SIGNING_AUTHORITY}."
  /usr/bin/grep -Fq "TeamIdentifier=${EXPECTED_MACOS_TEAM_ID}" <<<"${signature_details}" \
    || fail "The macOS CLI does not have the expected team identifier ${EXPECTED_MACOS_TEAM_ID}."
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      [[ $# -ge 2 ]] || fail "--version requires a value."
      VERSION="$2"
      shift 2
      ;;
    --build-number)
      [[ $# -ge 2 ]] || fail "--build-number requires a value."
      BUILD_NUMBER="$2"
      shift 2
      ;;
    --channel)
      [[ $# -ge 2 ]] || fail "--channel requires a value."
      CHANNEL="$2"
      shift 2
      ;;
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
    --release-url)
      [[ $# -ge 2 ]] || fail "--release-url requires a value."
      RELEASE_URL="$2"
      shift 2
      ;;
    --download-base-url)
      [[ $# -ge 2 ]] || fail "--download-base-url requires a value."
      DOWNLOAD_BASE_URL="${2%/}"
      shift 2
      ;;
    --secret-key-file)
      [[ $# -ge 2 && -n "$2" ]] || fail "--secret-key-file requires a path."
      SECRET_KEY_FILE="$2"
      shift 2
      ;;
    --setup-android)
      SETUP_ANDROID=1
      shift
      ;;
    --accept-android-licenses)
      ACCEPT_ANDROID_LICENSES=1
      shift
      ;;
    --android-target)
      [[ $# -ge 2 ]] || fail "--android-target requires a target."
      ANDROID_TARGET="$2"
      shift 2
      ;;
    --yes)
      ASSUME_YES=1
      shift
      ;;
    --no-setup)
      SKIP_SETUP=1
      shift
      ;;
    --no-path-update)
      UPDATE_PATH=0
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

absolute_path() {
  case "$1" in
    /*)
      printf '%s' "$1"
      ;;
    *)
      printf '%s/%s' "${PWD}" "$1"
      ;;
  esac
}

INSTALL_ROOT="$(absolute_path "${INSTALL_ROOT}")"
BIN_DIR="$(absolute_path "${BIN_DIR}")"
if [[ -f "${INSTALL_ROOT}/install.json" ]]; then
  INSTALL_EVENT="update"
else
  INSTALL_EVENT="install"
fi

if [[ -z "${CHANNEL}" ]]; then
  if [[ "${RELEASE_URL}" == *"/preview/"* || "${DOWNLOAD_BASE_URL}" == *"/preview"* ]]; then
    CHANNEL="preview"
  else
    CHANNEL="public"
  fi
fi
CHANNEL="$(printf '%s' "${CHANNEL}" | tr '[:upper:]' '[:lower:]')"
case "${CHANNEL}" in
  public)
    RELEASE_URL="${RELEASE_URL:-${PUBLIC_RELEASE_URL}}"
    DOWNLOAD_BASE_URL="${DOWNLOAD_BASE_URL:-${PUBLIC_DOWNLOAD_BASE_URL}}"
    ;;
  preview)
    RELEASE_URL="${RELEASE_URL:-${PREVIEW_RELEASE_URL}}"
    DOWNLOAD_BASE_URL="${DOWNLOAD_BASE_URL:-${PREVIEW_DOWNLOAD_BASE_URL}}"
    ;;
  *)
    fail "--channel must be 'public' or 'preview'."
    ;;
esac

finish_installer() {
  local result=$?
  if [[ -n "${TEMP_DIR:-}" ]]; then rm -rf "${TEMP_DIR}"; fi
  return "${result}"
}
trap finish_installer EXIT

case "$(uname -s)" in
  Darwin)
    PLATFORM="osx"
    ;;
  Linux)
    PLATFORM="linux"
    ;;
  *)
    fail "This installer supports macOS and Linux. On Windows, use install.ps1."
    ;;
esac

case "$(uname -m)" in
  x86_64|amd64)
    ARCHITECTURE="x64"
    ;;
  arm64|aarch64)
    ARCHITECTURE="arm64"
    ;;
  *)
    fail "Unsupported CPU architecture '$(uname -m)'."
    ;;
esac

RID="${PLATFORM}-${ARCHITECTURE}"

if [[ -z "${VERSION}" || -z "${BUILD_NUMBER}" ]]; then
  if command -v curl >/dev/null 2>&1; then
    RELEASE_JSON="$(curl -fsSL "${RELEASE_URL}")" \
      || fail "Could not download ${RELEASE_URL}."
  elif command -v wget >/dev/null 2>&1; then
    RELEASE_JSON="$(wget -qO- "${RELEASE_URL}")" \
      || fail "Could not download ${RELEASE_URL}."
  else
    fail "curl or wget is required."
  fi

  FEED_PRODUCT="$(printf '%s\n' "${RELEASE_JSON}" \
    | sed -n 's/.*"product"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    | head -n 1)"
  if [[ -n "${FEED_PRODUCT}" && "${FEED_PRODUCT}" != "cli" ]]; then
    fail "The release feed describes '${FEED_PRODUCT}', not the Ansight CLI."
  fi
  if [[ -z "${VERSION}" ]]; then
    VERSION="$(printf '%s\n' "${RELEASE_JSON}" \
      | sed -n 's/.*"cliVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
      | head -n 1)"
    if [[ -z "${VERSION}" ]]; then
      VERSION="$(printf '%s\n' "${RELEASE_JSON}" \
        | sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
        | head -n 1)"
    fi
  fi
  if [[ -z "${BUILD_NUMBER}" ]]; then
    BUILD_NUMBER="$(printf '%s\n' "${RELEASE_JSON}" \
      | sed -n 's/.*"cliBuildNumber"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' \
      | head -n 1)"
    if [[ -z "${BUILD_NUMBER}" ]]; then
      BUILD_NUMBER="$(printf '%s\n' "${RELEASE_JSON}" \
        | sed -n 's/.*"buildNumber"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' \
        | head -n 1)"
    fi
  fi
fi

[[ "${VERSION}" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] \
  || fail "Invalid release version '${VERSION}'."
[[ "${BUILD_NUMBER}" =~ ^[0-9]+$ && "${BUILD_NUMBER}" != "0" ]] \
  || fail "The release manifest does not contain a positive integer buildNumber."

ARCHIVE_NAME="ansight-cli-${RID}-${VERSION}-${BUILD_NUMBER}.tar.gz"
ARCHIVE_URL="${DOWNLOAD_BASE_URL%/}/${RID}/${VERSION}/${BUILD_NUMBER}/${ARCHIVE_NAME}"
CHECKSUM_URL="${ARCHIVE_URL}.sha256"
TEMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/ansight-install.XXXXXX")"

ARCHIVE_PATH="${TEMP_DIR}/${ARCHIVE_NAME}"
CHECKSUM_PATH="${ARCHIVE_PATH}.sha256"
PAYLOAD_DIR="${TEMP_DIR}/payload"

download() {
  local url="$1"
  local destination="$2"
  if command -v curl >/dev/null 2>&1; then
    # Curl's standard progress meter includes the total and downloaded sizes.
    # The compact --progress-bar display only reports a percentage.
    curl -fL --retry 3 --retry-delay 1 "${url}" -o "${destination}"
  elif command -v wget >/dev/null 2>&1; then
    wget -O "${destination}" "${url}"
  else
    fail "curl or wget is required."
  fi
}

echo "Installing Ansight CLI ${VERSION} (${BUILD_NUMBER}) for ${RID} from ${CHANNEL}..."
echo "Downloading the release checksum..."
download "${CHECKSUM_URL}" "${CHECKSUM_PATH}" \
  || fail "Could not download the checksum from ${CHECKSUM_URL}."
echo "Downloading the CLI archive..."
download "${ARCHIVE_URL}" "${ARCHIVE_PATH}" \
  || fail "Could not download the CLI archive from ${ARCHIVE_URL}."

echo "Verifying the CLI archive..."
EXPECTED_SHA256="$(awk 'match($0, /[0-9A-Fa-f]{64}/) { print substr($0, RSTART, RLENGTH); exit }' \
  "${CHECKSUM_PATH}" | tr '[:upper:]' '[:lower:]')"
[[ "${EXPECTED_SHA256}" =~ ^[0-9a-f]{64}$ ]] \
  || fail "The downloaded checksum file is invalid."

if command -v sha256sum >/dev/null 2>&1; then
  ACTUAL_SHA256="$(sha256sum "${ARCHIVE_PATH}" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
  ACTUAL_SHA256="$(shasum -a 256 "${ARCHIVE_PATH}" | awk '{print $1}')"
elif command -v openssl >/dev/null 2>&1; then
  ACTUAL_SHA256="$(openssl dgst -sha256 "${ARCHIVE_PATH}" | awk '{print $NF}')"
else
  fail "sha256sum, shasum, or openssl is required to validate the download."
fi
ACTUAL_SHA256="$(printf '%s' "${ACTUAL_SHA256}" | tr '[:upper:]' '[:lower:]')"

[[ "${ACTUAL_SHA256}" == "${EXPECTED_SHA256}" ]] \
  || fail "Checksum mismatch for ${ARCHIVE_NAME}. Expected ${EXPECTED_SHA256}, got ${ACTUAL_SHA256}."
echo "SHA-256 verified: ${ACTUAL_SHA256}"

echo "Inspecting and extracting the CLI archive..."
command -v tar >/dev/null 2>&1 || fail "tar is required to extract the CLI archive."
while IFS= read -r entry; do
  case "${entry}" in
    /*|../*|*/../*)
      fail "The CLI archive contains an unsafe path: ${entry}"
      ;;
  esac
done < <(tar -tzf "${ARCHIVE_PATH}")

mkdir -p "${PAYLOAD_DIR}"
tar -xzf "${ARCHIVE_PATH}" -C "${PAYLOAD_DIR}"
[[ -f "${PAYLOAD_DIR}/ansight" ]] || fail "The CLI archive does not contain ansight."
chmod +x "${PAYLOAD_DIR}/ansight"
verify_macos_cli_signature "${PAYLOAD_DIR}/ansight"

VERSIONS_DIR="${INSTALL_ROOT}/versions"
VERSION_DIR="${VERSIONS_DIR}/${VERSION}-${BUILD_NUMBER}-${RID}"
PENDING_DIR="${VERSIONS_DIR}/.install-${VERSION}-${BUILD_NUMBER}-${RID}-$$"
CURRENT_LINK="${INSTALL_ROOT}/current"
BIN_LINK="${BIN_DIR}/ansight"

mkdir -p "${VERSIONS_DIR}" "${BIN_DIR}"
rm -rf "${PENDING_DIR}"
mkdir -p "${PENDING_DIR}"
cp -R "${PAYLOAD_DIR}/." "${PENDING_DIR}/"
echo "Validating the downloaded CLI..."
"${PENDING_DIR}/ansight" help >/dev/null \
  || fail "The downloaded Ansight CLI did not start successfully."
verify_macos_cli_signature "${PENDING_DIR}/ansight"

echo "Activating the downloaded CLI..."
if [[ -e "${VERSION_DIR}" || -L "${VERSION_DIR}" ]]; then
  rm -rf "${VERSION_DIR}"
fi
mv "${PENDING_DIR}" "${VERSION_DIR}"

if [[ -d "${CURRENT_LINK}" && ! -L "${CURRENT_LINK}" ]]; then
  fail "${CURRENT_LINK} is a directory that was not created by this installer."
fi
ln -sfn "${VERSION_DIR}" "${CURRENT_LINK}"

if [[ -d "${BIN_LINK}" && ! -L "${BIN_LINK}" ]]; then
  fail "${BIN_LINK} is a directory and cannot be replaced with the Ansight PATH entry."
fi
ln -sfn "${CURRENT_LINK}/ansight" "${BIN_LINK}"

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  printf '%s' "${value}"
}

RECEIPT_PATH="${INSTALL_ROOT}/install.json"
RECEIPT_PENDING_PATH="${RECEIPT_PATH}.new-$$"
cat > "${RECEIPT_PENDING_PATH}" <<EOF
{
  "schema": "ansight.cli.installation/v1",
  "version": "$(json_escape "${VERSION}")",
  "buildNumber": ${BUILD_NUMBER},
  "channel": "$(json_escape "${CHANNEL}")",
  "rid": "$(json_escape "${RID}")",
  "releaseUrl": "$(json_escape "${RELEASE_URL}")",
  "downloadBaseUrl": "$(json_escape "${DOWNLOAD_BASE_URL%/}")",
  "archiveUrl": "$(json_escape "${ARCHIVE_URL}")",
  "sha256": "${ACTUAL_SHA256}",
  "installedAtUtc": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
  "installRoot": "$(json_escape "${INSTALL_ROOT}")",
  "binDirectory": "$(json_escape "${BIN_DIR}")"
}
EOF
mv "${RECEIPT_PENDING_PATH}" "${RECEIPT_PATH}"

update_shell_path() {
  local shell_name rc_file path_line
  shell_name="$(basename "${SHELL:-}")"
  case "${shell_name}" in
    zsh)
      rc_file="${HOME}/.zprofile"
      ;;
    bash)
      if [[ "${PLATFORM}" == "osx" ]]; then
        rc_file="${HOME}/.bash_profile"
      else
        rc_file="${HOME}/.bashrc"
      fi
      ;;
    *)
      echo "Add ${BIN_DIR} to PATH in your shell configuration."
      return
      ;;
  esac

  path_line="export PATH=\"${BIN_DIR}:\$PATH\""
  if [[ -f "${rc_file}" ]] && grep -Fqx "${path_line}" "${rc_file}"; then
    return
  fi

  {
    printf '\n# Added by the Ansight CLI installer\n'
    printf '%s\n' "${path_line}"
  } >> "${rc_file}"
  echo "Added ${BIN_DIR} to PATH in ${rc_file}."
}

case ":${PATH}:" in
  *":${BIN_DIR}:"*)
    ;;
  *)
    if [[ "${UPDATE_PATH}" -eq 1 ]]; then
      update_shell_path
    else
      echo "Add ${BIN_DIR} to PATH before opening a new terminal."
    fi
    ;;
esac

"${BIN_LINK}" help >/dev/null \
  || fail "The installed Ansight CLI did not start successfully."

prompt_yes_no() {
  local prompt="$1"
  local default_answer="$2"
  local suffix reply

  if [[ "${ASSUME_YES}" -eq 1 ]]; then
    return 0
  fi

  if [[ ! -r /dev/tty ]]; then
    if [[ "${PROMPT_UNAVAILABLE_REPORTED}" -eq 0 ]]; then
      echo "No interactive terminal is available; skipping optional setup."
      PROMPT_UNAVAILABLE_REPORTED=1
    fi
    return 1
  fi

  if [[ "${default_answer}" == "yes" ]]; then
    suffix="Y/n"
  else
    suffix="y/N"
  fi

  while true; do
    printf '%s [%s]: ' "${prompt}" "${suffix}" >/dev/tty
    IFS= read -r reply </dev/tty || return 1
    case "${reply}" in
      y|Y|yes|YES|Yes)
        return 0
        ;;
      n|N|no|NO|No)
        return 1
        ;;
      '')
        [[ "${default_answer}" == "yes" ]]
        return
        ;;
      *)
        echo "Please enter y or n." >/dev/tty
        ;;
    esac
  done
}

install_skill() {
  local label="$1"
  local destination_root="$2"
  local default_answer="$3"
  local skills_root="${CURRENT_LINK}/skills"
  local skills=(
    ansight-cli-setup
    ansight-app-inspection
    ansight-operate-live-app
    ansight-use-remote-app-tools
    ansight-investigate-session
    ansight-annotate-session
    ansight-assess-automation-readiness
    ansight-ui-testing
  )
  local skill source destination source_file relative_path source_count=0 skills_are_current=1

  [[ -d "${skills_root}" ]] || return
  for skill in "${skills[@]}"; do
    source="${skills_root}/${skill}"
    [[ -f "${source}/SKILL.md" ]] || continue
    source_count=$((source_count + 1))
    destination="${destination_root}/${skill}"
    while IFS= read -r -d '' source_file; do
      relative_path="${source_file#"${source}/"}"
      if [[ ! -f "${destination}/${relative_path}" ]] || ! cmp -s "${source_file}" "${destination}/${relative_path}"; then
        skills_are_current=0
        break 2
      fi
    done < <(find "${source}" -type f -print0)
  done

  if [[ "${source_count}" -eq "${#skills[@]}" && "${skills_are_current}" -eq 1 ]]; then
    echo "Ansight agent skills for ${label} are already current."
    return
  fi

  if [[ "${SKILL_INSTALL_NOTICE_SHOWN}" -eq 0 ]]; then
    echo "Ansight agent skills are necessary for your agent to work fully and provide the best experience with Ansight."
    echo "Installation is optional, but the experience may be degraded without them."
    echo "Review all skill instructions and supporting files: https://www.ansight.ai/skills"
    echo "Release manifests and verification: https://www.ansight.ai/docs/skills/disclosure"
    echo "This release's skill manifest (when available): ${ARCHIVE_URL}.skills.json"
    SKILL_INSTALL_NOTICE_SHOWN=1
  fi

  if prompt_yes_no "Install the Ansight agent skills for ${label}?" "${default_answer}"; then
    for skill in "${skills[@]}"; do
      source="${skills_root}/${skill}"
      [[ -f "${source}/SKILL.md" ]] || continue
      destination="${destination_root}/${skill}"
      mkdir -p "${destination}"
      cp -R "${source}/." "${destination}/"
      echo "Installed ${label} skill: ${destination}/SKILL.md"
    done
  fi
}

is_command_or_directory_present() {
  local command_name="$1"
  local directory="$2"
  command -v "${command_name}" >/dev/null 2>&1 || [[ -d "${directory}" ]]
}

xml_escape() {
  sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g"
}

enable_macos_startup() {
  local launch_agents_dir plist_path logs_dir executable_xml stdout_xml stderr_xml domain
  launch_agents_dir="${HOME}/Library/LaunchAgents"
  plist_path="${launch_agents_dir}/ai.ansight.host.plist"
  logs_dir="${HOME}/Library/Logs/Ansight"
  mkdir -p "${launch_agents_dir}" "${logs_dir}"
  executable_xml="$(printf '%s' "${CURRENT_LINK}/ansight" | xml_escape)"
  stdout_xml="$(printf '%s' "${logs_dir}/host.log" | xml_escape)"
  stderr_xml="$(printf '%s' "${logs_dir}/host-error.log" | xml_escape)"

  cat > "${plist_path}" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>ai.ansight.host</string>
  <key>ProgramArguments</key>
  <array>
    <string>${executable_xml}</string>
    <string>host</string>
    <string>run</string>
  </array>
  <key>RunAtLoad</key>
  <true/>
  <key>StandardOutPath</key>
  <string>${stdout_xml}</string>
  <key>StandardErrorPath</key>
  <string>${stderr_xml}</string>
</dict>
</plist>
EOF

  domain="gui/$(id -u)"
  launchctl bootout "${domain}" "${plist_path}" >/dev/null 2>&1 || true
  if "${CURRENT_LINK}/ansight" host status --json >/dev/null 2>&1; then
    echo "Installed the Ansight login item. The current host is already running."
  elif launchctl bootstrap "${domain}" "${plist_path}" >/dev/null 2>&1; then
    echo "Enabled and started the Ansight host with launchd."
  else
    echo "Installed ${plist_path}; launchd will load it at the next graphical login."
  fi
}

enable_linux_startup() {
  local systemd_dir unit_path autostart_dir desktop_path data_environment=""
  local service_environment=()
  if [[ -n "${ANSIGHT_DATA_DIR:-}" ]]; then
    # Explicitly carry a custom data directory into the service; never inherit shell secrets.
    local service_data_dir="${ANSIGHT_DATA_DIR}"
    [[ "${service_data_dir}" == /* ]] || service_data_dir="${PWD}/${service_data_dir}"
    [[ "${service_data_dir}" != *$'\n'* && "${service_data_dir}" != *$'\r'* ]] \
      || fail "ANSIGHT_DATA_DIR must not contain newlines."
    service_environment=("--setenv=ANSIGHT_DATA_DIR=${service_data_dir}")
    service_data_dir="${service_data_dir//\\/\\\\}"
    service_data_dir="${service_data_dir//\"/\\\"}"
    service_data_dir="${service_data_dir//%/%%}"
    data_environment="Environment=\"ANSIGHT_DATA_DIR=${service_data_dir}\""
  fi
  systemd_dir="${HOME}/.config/systemd/user"
  unit_path="${systemd_dir}/ansight-host.service"
  mkdir -p "${systemd_dir}"
  if [[ -f "${unit_path}" ]] && ! grep -Fq "ExecStart=\"${CURRENT_LINK}/ansight\" host run" "${unit_path}"; then
    echo "Preserved custom systemd unit: ${unit_path}. Configure its credential source and boot persistence manually."
    return
  fi
  # Preserve existing unit environment, credential directives, and administrator customizations.
  if [[ ! -f "${unit_path}" ]]; then
  cat > "${unit_path}" <<EOF
[Unit]
Description=Ansight local host
After=network.target
StartLimitIntervalSec=300
StartLimitBurst=3

[Service]
Type=simple
${data_environment}
ExecStart="${CURRENT_LINK}/ansight" host run
Restart=on-failure
RestartSec=5
RestartPreventExitStatus=5 6
ExecStartPre="${CURRENT_LINK}/ansight" doctor --credentials-only

[Install]
WantedBy=default.target
EOF
  fi

  if command -v systemctl >/dev/null 2>&1 \
    && systemctl --user daemon-reload >/dev/null 2>&1; then
    if ! systemd-run --user --wait --pipe --collect ${service_environment[@]+"${service_environment[@]}"} "${CURRENT_LINK}/ansight" doctor --credentials-only; then
      fail "Credentials are unavailable in the systemd user environment. Configure a persistent external key file for unattended use, then retry."
    fi
    if "${CURRENT_LINK}/ansight" host status --json >/dev/null 2>&1; then
      systemctl --user enable ansight-host.service >/dev/null
      echo "Enabled the Ansight systemd user service. The current host is already running."
    else
      systemctl --user enable --now ansight-host.service >/dev/null
      echo "Enabled and started the Ansight systemd user service."
    fi
    if command -v loginctl >/dev/null 2>&1; then
      if [[ "$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null || true)" != "yes" ]]; then
        if prompt_yes_no "Keep the user host service available after logout and start it at boot (enable systemd lingering)?" no; then
          loginctl enable-linger "$(id -un)" \
            || fail "Could not enable lingering. Ask an administrator to run: loginctl enable-linger $(id -un)"
        else
          echo "The service starts with your user session. For logout/reboot persistence run: loginctl enable-linger $(id -un)"
        fi
      fi
    else
      echo "Boot persistence could not be verified: ask your administrator to enable systemd user lingering."
    fi
    return
  fi

  if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
    fail "No systemd user manager is available on this headless host. Enable a user manager and lingering, then retry startup setup."
  fi
  autostart_dir="${HOME}/.config/autostart"
  desktop_path="${autostart_dir}/ansight-host.desktop"
  mkdir -p "${autostart_dir}"
  cat > "${desktop_path}" <<EOF
[Desktop Entry]
Type=Application
Name=Ansight Host
Comment=Start the local Ansight host
Exec="${CURRENT_LINK}/ansight" host run
Terminal=false
X-GNOME-Autostart-enabled=true
EOF
  echo "Installed the Ansight desktop login item: ${desktop_path}"
}

run_android_setup() {
  local setup_args=(setup android --target "${ANDROID_TARGET}")
  [[ "${ASSUME_YES}" -eq 0 ]] || setup_args+=(--yes)
  [[ "${ACCEPT_ANDROID_LICENSES}" -eq 0 ]] || setup_args+=(--accept-android-licenses)
  if [[ "${ASSUME_YES}" -eq 0 ]] && ( : </dev/tty ) 2>/dev/null; then
    "${CURRENT_LINK}/ansight" "${setup_args[@]}" </dev/tty
  else
    "${CURRENT_LINK}/ansight" "${setup_args[@]}"
  fi
}

if [[ "${SETUP_ANDROID}" -eq 1 ]]; then
  [[ "${SKIP_SETUP}" -eq 0 ]] || fail "--setup-android cannot be combined with --no-setup."
  ANDROID_TARGET="${ANDROID_TARGET:-android-device}"
fi

case "${ANDROID_TARGET}" in
  ""|android-device|android-emulator) ;;
  *) fail "--android-target must be android-device or android-emulator." ;;
esac

if [[ "${PLATFORM}" == "linux" ]]; then
  if [[ -n "${SECRET_KEY_FILE}" ]]; then
    "${CURRENT_LINK}/ansight" config credentials --key-file "${SECRET_KEY_FILE}" --non-interactive \
      || fail "The CLI is installed, but external credential setup failed. Existing keys were preserved."
  elif [[ "${SKIP_SETUP}" -eq 0 ]]; then
    if ! "${CURRENT_LINK}/ansight" config credentials --non-interactive; then
      if [[ "${ASSUME_YES}" -eq 0 ]] && ( : </dev/tty ) 2>/dev/null; then
        "${CURRENT_LINK}/ansight" config credentials </dev/tty \
          || echo "Credential setup remains incomplete."
      else
        echo "Headless setup requires --secret-key-file <persistent-external-key-path>. No key was generated."
      fi
    fi
  fi
fi

if "${CURRENT_LINK}/ansight" doctor --credentials-only; then CREDENTIALS_READY=1; fi
if "${CURRENT_LINK}/ansight" doctor; then CORE_READY=1; fi
if [[ "${SETUP_ANDROID}" -eq 1 ]]; then
  run_android_setup || fail "Optional Android setup did not complete. Rerun: ansight setup android --target ${ANDROID_TARGET}"
fi
if [[ -n "${ANDROID_TARGET}" ]]; then
  if [[ "${SETUP_ANDROID}" -eq 0 && "${SKIP_SETUP}" -eq 0 && "${ASSUME_YES}" -eq 0 ]] \
    && ! "${CURRENT_LINK}/ansight" doctor --target "${ANDROID_TARGET}" \
    && prompt_yes_no "Discover and configure missing Android tools for ${ANDROID_TARGET}?" no; then
    run_android_setup || echo "Android setup remains incomplete; run ansight setup android later."
  fi
  if "${CURRENT_LINK}/ansight" doctor --target "${ANDROID_TARGET}"; then
    ANDROID_READY="${ANDROID_TARGET} prerequisites ready; screenshot and input capture not tested"
  else
    ANDROID_READY="${ANDROID_TARGET} prerequisites incomplete; see diagnostics above"
  fi
fi

# Upgrade our existing service during non-interactive updates.
# Do not change custom services or enable a service the user previously disabled.
if [[ "${PLATFORM}" == "linux" ]]; then
  EXISTING_UNIT="${HOME}/.config/systemd/user/ansight-host.service"
  if [[ -f "${EXISTING_UNIT}" ]] \
    && grep -Fq "ExecStart=\"${CURRENT_LINK}/ansight\" host run" "${EXISTING_UNIT}" \
    && ! grep -q '^RestartPreventExitStatus=' "${EXISTING_UNIT}"; then
    UNIT_TEMP="$(mktemp "${EXISTING_UNIT}.XXXXXX")"
    sed '/^Restart=on-failure$/a\
RestartPreventExitStatus=6
' "${EXISTING_UNIT}" > "${UNIT_TEMP}"
    chmod 644 "${UNIT_TEMP}"
    mv "${UNIT_TEMP}" "${EXISTING_UNIT}"
    if command -v systemctl >/dev/null 2>&1; then
      systemctl --user daemon-reload >/dev/null 2>&1 || true
    fi
  fi
fi

if [[ "${SKIP_SETUP}" -eq 0 && "${CREDENTIALS_READY}" -eq 1 ]]; then
  if is_command_or_directory_present codex "${HOME}/.codex"; then
    CODEX_DEFAULT=yes
  else
    CODEX_DEFAULT=no
  fi
  if is_command_or_directory_present cursor "${HOME}/.cursor"; then
    CURSOR_DEFAULT=yes
  else
    CURSOR_DEFAULT=no
  fi
  if is_command_or_directory_present claude "${HOME}/.claude"; then
    CLAUDE_DEFAULT=yes
  else
    CLAUDE_DEFAULT=no
  fi

  install_skill "Codex and shared Agent Skills clients" "${HOME}/.agents/skills" "${CODEX_DEFAULT}"
  install_skill "Cursor" "${HOME}/.cursor/skills" "${CURSOR_DEFAULT}"
  install_skill "Claude Code" "${HOME}/.claude/skills" "${CLAUDE_DEFAULT}"

  echo "Local developer tools are free and require no Ansight account."
  echo "For cloud sharing, uploads or delegated jobs, run: ansight account login"
  if prompt_yes_no "Start the Ansight host automatically on this computer?" yes; then
    if [[ "${PLATFORM}" == "osx" ]]; then
      enable_macos_startup
    else
      enable_linux_startup
    fi
  else
    echo "Start the host and open the local player when needed with: ansight host run --open"
  fi
fi


echo
echo "Ansight CLI ${VERSION} (${BUILD_NUMBER}) is installed from ${CHANNEL}."
echo "Credentials ready: ${CREDENTIALS_READY}"
echo "Required checks passed: ${CORE_READY}"
echo "Local tools: ready without an account or subscription"
echo "Optional cloud sign-in: ansight account login"
echo "Android: ${ANDROID_READY}"
echo "Optional Android setup later: ansight setup android [--target android-emulator]"
echo "Executable: ${BIN_LINK}"
echo "Payload: ${VERSION_DIR}"
echo "Update: curl -fsSL https://www.ansight.ai/update.sh | bash"
echo "Uninstall: curl -fsSL https://www.ansight.ai/uninstall.sh | bash"
if [[ ":${PATH}:" != *":${BIN_DIR}:"* ]]; then
  echo "Open a new terminal, or add ${BIN_DIR} to PATH for this session."
fi

if [[ "${SKIP_SETUP}" -eq 0 ]]; then
  [[ "${CREDENTIALS_READY}" -eq 1 && "${CORE_READY}" -eq 1 ]] \
    || fail "CLI files installed, but required setup checks failed. Resolve the diagnostics above and rerun setup."
  if [[ -n "${ANDROID_TARGET}" && "${ANDROID_READY}" == *"prerequisites incomplete"* ]]; then
    fail "CLI files installed, but the selected Android workflow is not ready."
  fi
fi
"${BIN_LINK}" analytics lifecycle "${INSTALL_EVENT}" \
  --channel "${CHANNEL}" --rid "${RID}" --distribution shell --silent >/dev/null 2>&1 || true
