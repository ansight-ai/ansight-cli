import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const project = fileURLToPath(new URL('./bundle-scrcpy.proj', import.meta.url));
const binaryArchive = 'scrcpy-win64-v3.3.1.zip';
const sourceArchives = [
  'scrcpy-3.3.1.tar.gz', 'ffmpeg-7.1.1.tar.xz', 'SDL-2.32.8.tar.gz',
  'libusb-1.0.29.tar.gz', 'dav1d-1.5.0.tar.gz',
];

function runBundler(output, cache, rid = 'win-x64') {
  return spawnSync('dotnet', [
    'msbuild', project, '-nologo', '-verbosity:minimal',
    `-p:RuntimeIdentifier=${rid}`, `-p:PublishDir=${output}`, `-p:ScrcpyCacheDirectory=${cache}`,
  ], { encoding: 'utf8', timeout: 60_000 });
}

test('rejects a tampered cached download before replacing an installed bundle', () => {
  const directory = mkdtempSync(path.join(tmpdir(), 'ansight scrcpy bad '));
  try {
    const cache = path.join(directory, 'cache');
    const output = path.join(directory, 'publish');
    const existing = path.join(output, 'tools', 'scrcpy', 'scrcpy.exe');
    mkdirSync(cache);
    mkdirSync(path.dirname(existing), { recursive: true });
    writeFileSync(existing, 'previous valid install');
    for (const asset of [binaryArchive, ...sourceArchives]) {
      writeFileSync(path.join(cache, asset), 'untrusted download');
    }

    const result = runBundler(output, cache);

    assert.notEqual(result.status, 0);
    assert.match(result.stdout + result.stderr, /MSB3952/);
    assert.equal(readFileSync(existing, 'utf8'), 'previous valid install');
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

const assetCache = process.env.ANSIGHT_SCRCPY_TEST_ASSETS;
for (const rid of ['win-x64', 'win-arm64']) {
  test(`packages the official distribution for ${rid}, including server, DLLs and notices`, {
    skip: !assetCache && 'Set ANSIGHT_SCRCPY_TEST_ASSETS to the pinned downloaded assets for the offline archive test.',
  }, () => {
    const directory = mkdtempSync(path.join(tmpdir(), 'ansight scrcpy package '));
    try {
      const result = runBundler(directory, assetCache, rid);
      assert.equal(result.status, 0, result.stdout + result.stderr);
      const bundle = path.join(directory, 'tools', 'scrcpy');
      const manifest = JSON.parse(readFileSync(path.join(bundle, 'bundle.json'), 'utf8').replace(/^\uFEFF/, ''));
      assert.equal(manifest.version, '3.3.1');
      assert.equal(manifest.architecture, 'x64');
      assert.equal(Object.keys(manifest.files).length, 11);
      assert.equal(manifest.files['scrcpy-server'].toLowerCase(), 'a0f70b20aa4998fbf658c94118cd6c8dab6abbb0647a3bdab344d70bc1ebcbb8');
      for (const [name, checksum] of Object.entries(manifest.files)) {
        const actual = createHash('sha256').update(readFileSync(path.join(bundle, name))).digest('hex');
        assert.equal(actual, checksum.toLowerCase(), name);
      }
      assert.match(readFileSync(path.join(bundle, 'licenses', 'NOTICE.txt'), 'utf8'), /LGPL 2.1/);
      assert.ok(readFileSync(path.join(bundle, 'licenses', 'Android-platform-tools-NOTICE.txt')).length > 0);
      for (const archive of sourceArchives) {
        assert.ok(readFileSync(path.join(bundle, 'sources', archive)).length > 0, archive);
      }
      assert.ok(existsSync(path.join(assetCache, binaryArchive)), 'external asset cache is retained');
      assert.equal(existsSync(path.join(directory, '.scrcpy-extract')), false);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });
}
