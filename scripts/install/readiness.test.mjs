import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, chmodSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const source = readFileSync(new URL('./install.sh', import.meta.url), 'utf8');
const powershellSource = readFileSync(new URL('./install.ps1', import.meta.url), 'utf8');
const setup = source.slice(source.indexOf('run_android_setup() {'), source.indexOf('# Upgrade our existing service'));
const completion = source.slice(source.lastIndexOf('if [[ "${SKIP_SETUP}" -eq 0 ]]; then'));

function assertAppearsBefore(value, first, second) {
  const firstIndex = value.indexOf(first);
  const secondIndex = value.indexOf(second);
  assert.notEqual(firstIndex, -1, `Missing ${first}`);
  assert.notEqual(secondIndex, -1, `Missing ${second}`);
  assert.ok(firstIndex < secondIndex, `Expected ${first} before ${second}`);
}

test('agent skills and local host startup require no account on every platform', () => {
  const unixSetup = source.slice(source.indexOf('if [[ "${SKIP_SETUP}" -eq 0 && "${CREDENTIALS_READY}" -eq 1 ]]; then'));
  assertAppearsBefore(unixSetup, 'install_skill "Codex and shared Agent Skills clients"', 'enable_linux_startup');
  assert.doesNotMatch(unixSetup, /auth status|account access|PRODUCT_AUTHORIZED|AUTHENTICATED/);
  assert.doesNotMatch(unixSetup, /Sign in to your Ansight account now/);
  assert.match(unixSetup, /require no Ansight account/);

  const windowsSetup = powershellSource.slice(powershellSource.indexOf('if (-not $NoSetup) {'));
  assertAppearsBefore(windowsSetup, 'Install-AnsightSkill `', 'Enable-AnsightStartup `');
  assert.doesNotMatch(windowsSetup, /auth status|account access|productAuthorized/);
  assert.doesNotMatch(windowsSetup, /Sign in to your Ansight account now/);
  assert.match(windowsSetup, /require no Ansight account/);
});

function run({ credentials = 0, core = 0, android = 0, skip = 0, key = '', target = '', setupAndroid = 0, acceptLicenses = 0 } = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'ansight-installer-'));
  try {
    const log = join(directory, 'calls');
    const cli = join(directory, 'ansight');
    writeFileSync(cli, `#!/bin/bash
printf '%s\\n' "$*" >> "$CALL_LOG"
case "$*" in
  'config credentials'*) exit "$CREDENTIAL_EXIT" ;;
  'doctor --credentials-only') exit "$CREDENTIAL_EXIT" ;;
  'doctor --target '*) exit "$ANDROID_EXIT" ;;
  doctor) exit "$CORE_EXIT" ;;
esac
`);
    chmodSync(cli, 0o700);
    const script = `set -euo pipefail
fail() { echo "$*"; exit 1; }
CREDENTIALS_READY=0
CORE_READY=0
ANDROID_READY='not tested'
${setup}
${completion}
`;
    const result = spawnSync('bash', ['-c', script], {
      encoding: 'utf8', timeout: 10000,
      env: { ...process.env, PLATFORM: 'linux', CURRENT_LINK: directory, BIN_LINK: cli,
        CALL_LOG: log, CREDENTIAL_EXIT: String(credentials), CORE_EXIT: String(core), ANDROID_EXIT: String(android),
        SKIP_SETUP: String(skip), SETUP_ANDROID: String(setupAndroid), ACCEPT_ANDROID_LICENSES: String(acceptLicenses), ASSUME_YES: '1', SECRET_KEY_FILE: key, ANDROID_TARGET: target,
        INSTALL_EVENT: 'install', CHANNEL: 'public', RID: 'linux-x64' }
    });
    return { ...result, calls: readFileSync(log, 'utf8') };
  } finally { rmSync(directory, { recursive: true, force: true }); }
}

test('setup validates credentials before doctor and reports completion only after required checks', () => {
  const result = run({ key: '/external/path with spaces/key' });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.calls, /^config credentials --key-file \/external\/path with spaces\/key --non-interactive\ndoctor --credentials-only\ndoctor\n/);
  assert.match(result.calls, /analytics lifecycle install --channel public --rid linux-x64 --distribution shell --silent/);
});

test('a credential setup failure cannot report completion', () => {
  const result = run({ credentials: 5, key: '/external/key' });
  assert.equal(result.status, 1);
  assert.doesNotMatch(result.calls, /analytics lifecycle/);
});

test('a failed required diagnostic prevents setup success', () => {
  const result = run({ core: 5 });
  assert.equal(result.status, 1);
  assert.match(result.stdout, /required setup checks failed/);
  assert.doesNotMatch(result.calls, /analytics lifecycle/);
});

test('explicit emulator readiness failure prevents workflow success', () => {
  const result = run({ target: 'android-emulator', android: 5 });
  assert.equal(result.status, 1);
  assert.match(result.calls, /doctor --target android-emulator/);
  assert.match(result.stdout, /selected Android workflow is not ready/);
  assert.doesNotMatch(result.calls, /analytics lifecycle/);
});

test('install-only reports missing prerequisites without configuring credentials', () => {
  const result = run({ skip: 1, credentials: 5, core: 5 });
  assert.equal(result.status, 0, result.stderr);
  assert.doesNotMatch(result.calls, /config credentials/);
  assert.match(result.calls, /doctor --credentials-only/);
});

const startup = source.slice(source.indexOf('enable_linux_startup() {'), source.indexOf('run_android_setup() {'))
  .replaceAll('${HOME}', '${TEST_INSTALL_HOME}');

for (const preflight of [0, 5]) {
  test(`systemd startup ${preflight === 0 ? 'uses saved settings and a custom data directory' : 'rejects unavailable service credentials'}`, () => {
    const directory = mkdtempSync(join(tmpdir(), 'ansight-service-'));
    try {
      const script = `set -eu
fail() { echo "$*"; exit 1; }
systemctl() { printf '%s\\n' "$*" >> "$SERVICE_LOG"; }
systemd-run() { printf '%s\\n' "$*" >> "$PREFLIGHT_LOG"; return "$PREFLIGHT_EXIT"; }
loginctl() { echo yes; }
prompt_yes_no() { return 1; }
${startup}
enable_linux_startup
`;
      const cli = join(directory, 'ansight');
      writeFileSync(cli, '#!/bin/sh\nexit 1\n');
      chmodSync(cli, 0o700);
      const log = join(directory, 'service-calls');
      const probeLog = join(directory, 'probe-calls');
      const data = join(directory, 'custom state');
      const result = spawnSync('bash', ['-c', script], { encoding: 'utf8', timeout: 10000,
        env: { ...process.env, TEST_INSTALL_HOME: directory, CURRENT_LINK: directory,
          SERVICE_LOG: log, PREFLIGHT_LOG: probeLog, PREFLIGHT_EXIT: String(preflight), ANSIGHT_DATA_DIR: data } });
      assert.equal(result.status, preflight === 0 ? 0 : 1, result.stderr);
      const unit = readFileSync(join(directory, '.config/systemd/user/ansight-host.service'), 'utf8');
      assert.match(unit, /ExecStartPre=.*doctor --credentials-only/);
      assert.ok(unit.includes(`Environment="ANSIGHT_DATA_DIR=${data}"`));
      assert.ok(readFileSync(probeLog, 'utf8').includes(`--setenv=ANSIGHT_DATA_DIR=${data}`));
      const serviceCalls = readFileSync(log, 'utf8');
      if (preflight === 0) assert.match(serviceCalls, /enable --now ansight-host.service/);
      else assert.doesNotMatch(serviceCalls, /enable/);
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });
}


test('Android fallback is opt-in and generic yes does not accept SDK licenses', () => {
  const normal = run();
  assert.doesNotMatch(normal.calls, /setup android/);
  const fallback = run({ setupAndroid: 1 });
  assert.equal(fallback.status, 0, fallback.stderr);
  assert.match(fallback.calls, /setup android --target android-device --yes/);
  assert.doesNotMatch(fallback.calls, /--accept-android-licenses/);
  const accepted = run({ setupAndroid: 1, acceptLicenses: 1 });
  assert.match(accepted.calls, /setup android --target android-device --yes --accept-android-licenses/);
});
