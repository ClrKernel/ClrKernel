#!/usr/bin/env node
/**
 * changelog.js — drafts a CHANGELOG.md section from commit messages.
 *
 * Usage (from editors/vscode/):
 *   npm run changelog
 *
 * Collects commits since the last EXTENSION release tag (ext-v*) that touched
 * editors/vscode/ or src/ClrKernel.Server/, groups them by conventional-commit
 * type, and prepends a "## [<version>] - <date>" section for the version
 * currently in package.json. The draft is meant to be EDITED before
 * committing — commit messages are written for developers; changelog entries
 * are for users. Delete internal noise, reword the rest.
 *
 * Mapping: feat -> Added, fix -> Fixed, perf -> Changed, docs -> Changed,
 * everything else -> Internal (usually delete these lines).
 *
 * Release flow:
 *   1. bump "version" in package.json
 *   2. npm run changelog && edit CHANGELOG.md
 *   3. commit, then: git tag ext-v<version>
 *      (NOT v<version> — plain v* tags trigger the NuGet release workflow)
 *   4. vsce package && vsce publish
 */

const { execFileSync } = require('child_process');
const path = require('path');
const fs = require('fs');

const EXT_DIR = path.resolve(__dirname, '..');
const CHANGELOG = path.join(EXT_DIR, 'CHANGELOG.md');
const version = JSON.parse(fs.readFileSync(path.join(EXT_DIR, 'package.json'), 'utf8')).version;

function git(args) {
    return execFileSync('git', args, { cwd: EXT_DIR, encoding: 'utf8' }).trim();
}

// Range: since the last ext-v* tag, or all history if none exists yet
let range = 'HEAD';
try {
    const lastTag = git(['describe', '--tags', '--abbrev=0', '--match', 'ext-v*']);
    range = `${lastTag}..HEAD`;
} catch { /* no extension tags yet */ }

// Extension-facing paths only: the extension itself plus the server it talks
// to. Kernel/Core commits ship through the NuGet release notes instead.
const repoRoot = git(['rev-parse', '--show-toplevel']);
const paths = ['editors/vscode', 'src/ClrKernel.Server'].map(p => path.join(repoRoot, p));
const log = git(['log', range, '--format=%s', '--', ...paths]);
if (!log) {
    console.log(`No commits in ${range} — nothing to draft.`);
    process.exit(0);
}

const sections = { Added: [], Fixed: [], Changed: [], Internal: [] };
for (const subject of log.split('\n')) {
    const m = subject.match(/^(\w+)(\([^)]*\))?!?:\s*(.*)$/);
    const type = m ? m[1] : '';
    const text = m ? m[3] : subject;
    if (type === 'feat') sections.Added.push(text);
    else if (type === 'fix') sections.Fixed.push(text);
    else if (type === 'perf' || type === 'docs') sections.Changed.push(text);
    else sections.Internal.push(text);
}

const today = new Date().toISOString().slice(0, 10);
let draft = `## [${version}] - ${today}\n`;
for (const [heading, items] of Object.entries(sections)) {
    if (!items.length) continue;
    draft += `\n### ${heading}\n\n`;
    for (const item of items) draft += `- ${item}\n`;
}
draft += '\n';

const existing = fs.readFileSync(CHANGELOG, 'utf8');
if (existing.includes(`## [${version}]`)) {
    console.error(`CHANGELOG.md already has a section for ${version} — bump package.json first.`);
    process.exit(1);
}
// Insert after the intro (before the first existing "## [" heading)
const idx = existing.indexOf('## [');
const updated = idx >= 0
    ? existing.slice(0, idx) + draft + existing.slice(idx)
    : existing + '\n' + draft;
fs.writeFileSync(CHANGELOG, updated);

console.log(`Drafted ${version} section in CHANGELOG.md from ${range}.`);
console.log('Now EDIT it: reword for users, delete Internal noise, then commit and tag ext-v' + version + '.');
