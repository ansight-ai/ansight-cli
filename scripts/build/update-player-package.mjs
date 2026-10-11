#!/usr/bin/env node
import { cpSync, mkdirSync, readdirSync, readFileSync, unlinkSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawnSync } from 'node:child_process'

const cliRoot = fileURLToPath(new URL('../..', import.meta.url))
if (process.argv.length !== 3) throw new Error('Usage: node scripts/build/update-player-package.mjs <ansight-player-checkout>')
const source = resolve(process.argv[2])
const manifest = JSON.parse(readFileSync(join(source, 'package.json'), 'utf8'))
if (manifest.name !== '@ansight/player') throw new Error('Expected the standalone @ansight/player repository.')
const archive = `ansight-player-${manifest.version}.tgz`
const destination = join(cliRoot, 'vendor')
mkdirSync(destination, { recursive: true })
cpSync(join(source, 'products', archive), join(destination, archive))

const result = spawnSync('npm', ['install', '--save-dev', '--save-exact', '--ignore-scripts', '--no-audit', '--no-fund', `@ansight/player@file:vendor/${archive}`], { cwd: cliRoot, stdio: 'inherit', shell: process.platform === 'win32' })
if (result.status === 0) {
  for (const entry of readdirSync(destination, { withFileTypes: true })) {
    if (entry.isFile() && /^ansight-player-.+\.tgz$/.test(entry.name) && entry.name !== archive) {
      unlinkSync(join(destination, entry.name))
    }
  }
}
process.exitCode = result.status ?? 1
