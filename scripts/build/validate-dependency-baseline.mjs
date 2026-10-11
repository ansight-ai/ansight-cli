import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

function version(value) {
  if (typeof value !== 'string') throw new Error('Versions must be strings.');
  const match = /^(\d+(?:\.\d+){1,3})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.exec(value);
  if (!match || match[0] !== value) throw new Error(`Invalid dependency version: ${value}`);
  const parts = match[1].split('.').map(Number);
  if (parts.some(part => part > 2147483647)) throw new Error(`Invalid dependency version: ${value}`);
  while (parts.length < 4) parts.push(0);
  return { parts, prerelease: match[2]?.split('.') ?? [] };
}

function compare(left, right) {
  for (let index = 0; index < 4; index++) {
    if (left.parts[index] !== right.parts[index]) return left.parts[index] - right.parts[index];
  }
  if (!left.prerelease.length || !right.prerelease.length)
    return Number(!left.prerelease.length) - Number(!right.prerelease.length);
  for (let index = 0; index < Math.min(left.prerelease.length, right.prerelease.length); index++) {
    const a = left.prerelease[index], b = right.prerelease[index];
    const numericA = /^\d+$/.test(a), numericB = /^\d+$/.test(b);
    if (numericA && numericB) {
      if (BigInt(a) !== BigInt(b)) return BigInt(a) < BigInt(b) ? -1 : 1;
    } else if (numericA !== numericB) return numericA ? -1 : 1;
    else if (a !== b) return a < b ? -1 : 1;
  }
  return left.prerelease.length - right.prerelease.length;
}

function object(value, keys, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error(`${label} must be an object.`);
  for (const key of Object.keys(value))
    if (!keys.includes(key)) throw new Error(`Unknown ${label} field: ${key}`);
}

function text(value, label) {
  if (typeof value !== 'string' || !value.trim()) throw new Error(`${label} must be a nonempty string.`);
}

export function validateBaseline(baseline) {
  object(baseline, ['schemaVersion', 'recordedOn', 'recordedPlatform', 'basis', 'notes', 'dependencies'], 'baseline');
  if (baseline.schemaVersion !== 1) throw new Error('Expected dependency baseline schemaVersion 1.');
  text(baseline.basis, 'basis');
  text(baseline.notes, 'notes');
  text(baseline.recordedPlatform, 'recordedPlatform');
  if (typeof baseline.recordedOn !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(baseline.recordedOn)
      || !Number.isFinite(Date.parse(baseline.recordedOn))
      || new Date(baseline.recordedOn).toISOString().slice(0, 10) !== baseline.recordedOn)
    throw new Error('recordedOn must be an ISO calendar date.');
  if (!Array.isArray(baseline.dependencies) || !baseline.dependencies.length)
    throw new Error('The dependency baseline must contain dependencies.');
  const ids = new Set();
  for (const dependency of baseline.dependencies) {
    object(dependency, ['id', 'versions', 'platforms', 'usedBy', 'providers', 'basis'], 'dependency');
    if (typeof dependency.id !== 'string' || !/^[a-z][a-z0-9-]*$/.test(dependency.id) || ids.has(dependency.id))
      throw new Error(`Invalid or duplicate dependency id: ${dependency.id}`);
    ids.add(dependency.id);
    object(dependency.versions, ['min', 'target', 'maxExclusive'], `${dependency.id}.versions`);
    const { min, target, maxExclusive } = dependency.versions;
    if (compare(version(min), version(target)) > 0)
      throw new Error(`${dependency.id}: min must be <= target.`);
    if (maxExclusive != null && compare(version(target), version(maxExclusive)) >= 0)
      throw new Error(`${dependency.id}: target must be < maxExclusive.`);
    if (!Array.isArray(dependency.platforms) || !dependency.platforms.length
        || dependency.platforms.some(platform => !['macos', 'linux', 'windows'].includes(platform))
        || new Set(dependency.platforms).size !== dependency.platforms.length)
      throw new Error(`${dependency.id}: specify distinct macos, linux and/or windows platforms.`);
    text(dependency.usedBy, `${dependency.id}.usedBy`);
    if (dependency.basis != null) text(dependency.basis, `${dependency.id}.basis`);
    if (!dependency.providers || typeof dependency.providers !== 'object' || Array.isArray(dependency.providers))
      throw new Error(`${dependency.id}.providers must be an object.`);
    for (const value of Object.values(dependency.providers)) text(value, `${dependency.id}.providers`);
  }
  return baseline;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const file = process.argv[2];
    if (!file) throw new Error('Usage: node validate-dependency-baseline.mjs <manifest.json>');
    const baseline = validateBaseline(JSON.parse(readFileSync(file, 'utf8')));
    console.log(`Dependency baseline validated: ${baseline.dependencies.length} advisory entries.`);
  } catch (error) {
    console.error(`Invalid Ansight dependency baseline: ${error.message}`);
    process.exitCode = 1;
  }
}
