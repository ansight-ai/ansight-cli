import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { validateBaseline } from './validate-dependency-baseline.mjs';

const baseline = () => JSON.parse(readFileSync(new URL('../../ansight.dependencies.json', import.meta.url), 'utf8'));

test('the release baseline is valid and includes every declared setup dependency', () => {
  const result = validateBaseline(baseline());
  for (const id of ['node', 'npm', 'tesseract', 'scrcpy', 'mitmdump', 'axe', 'blackhole', 'appium', 'xcuitest',
    'dotnet', 'dotnet-trace', 'dotnet-dsrouter', 'java', 'adb', 'emulator', 'android-commandline-tools', 'xcode'])
    assert.ok(result.dependencies.some(dependency => dependency.id === id));
});

test('range ordering compares numeric components and prereleases correctly', () => {
  const data = baseline();
  data.dependencies[0].versions = { min: '3.9.0', target: '3.10.0', maxExclusive: '4.0.0' };
  assert.doesNotThrow(() => validateBaseline(data));
  data.dependencies[0].versions = { min: '3.0.0-rc.2', target: '3.0.0-rc.10', maxExclusive: '3.0.0' };
  assert.doesNotThrow(() => validateBaseline(data));
});

for (const versions of [
  { min: '3.10', target: '3.9' },
  { min: '3.0', target: '4.0', maxExclusive: '4.0.0' },
  { min: '3.0', target: '3.0-rc.1' },
  { min: 'latest', target: '3.0' },
  { min: '3.0', target: '3.0\n' },
  { min: '3.0', target: '3.0', max: '4.0' }
]) test(`rejects invalid policy ${JSON.stringify(versions)}`, () => {
  const data = baseline();
  data.dependencies[0].versions = versions;
  assert.throws(() => validateBaseline(data));
});

test('rejects duplicate ids and malformed entries before producing a release', () => {
  const data = baseline();
  data.dependencies.push(data.dependencies[0]);
  assert.throws(() => validateBaseline(data), /duplicate/);
  assert.throws(() => validateBaseline({ ...baseline(), dependencies: [] }));
  assert.throws(() => validateBaseline({ ...baseline(), recordedOn: '2026-02-30' }));
});
