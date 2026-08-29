// Fails the build if any internal link in dist/ points at a page or an anchor that
// was not built. Worth a script and not a one-off: the API section's 4,000-odd
// cross-references are rewritten by fixApi() from DocFX's own `Foo.Bar.md` links,
// so a DocFX upgrade that changes its output shape breaks every one of them —
// silently, because the site still builds.
//
//   node scripts/check-links.mjs [dist-dir]

import { promises as fs } from 'node:fs';
import path from 'node:path';

const dist = path.resolve(process.argv[2] ?? 'dist');
const base = process.env.DOCS_BASE ?? '/ClrKernel';

async function walk(dir, out = []) {
  for (const e of await fs.readdir(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) await walk(p, out); else out.push(p);
  }
  return out;
}

const files = await walk(dist);
const urlOf = (f) => '/' + path.relative(dist, f).split(path.sep).join('/');
const present = new Set(files.map(urlOf));
const html = files.filter((f) => f.endsWith('.html'));

// id="…" on the built page: heading slugs and the anchors DocFX writes for members.
const anchors = new Map();
for (const f of html) {
  const ids = new Set();
  for (const m of (await fs.readFile(f, 'utf8')).matchAll(/\bid="([^"]+)"/g)) ids.add(m[1]);
  anchors.set(urlOf(f).replace(/index\.html$/, ''), ids);
}

const broken = new Map();
const note = (link, why, from) => {
  const key = `${link} (${why})`;
  broken.set(key, (broken.get(key) ?? new Set()).add(path.relative(dist, from)));
};

for (const f of html) {
  const text = await fs.readFile(f, 'utf8');
  for (const [, href] of text.matchAll(/(?:href|src)="([^"]+)"/g)) {
    if (/^(https?:|mailto:|data:|#|\/\/)/.test(href)) continue;
    if (!href.startsWith(base)) continue;
    const [rawPath, frag] = href.split('#');
    let url = rawPath.split('?')[0].slice(base.length) || '/';
    const candidates = [url, url.replace(/\/$/, '') + '/index.html', url + '/index.html', url + 'index.html'];
    const hit = candidates.find((c) => present.has(c));
    if (!hit) { note(href, 'no such page', f); continue; }
    if (!frag) continue;
    const ids = anchors.get(hit.replace(/index\.html$/, ''));
    if (ids && !ids.has(decodeURIComponent(frag))) note(href, 'no such anchor', f);
  }
}

if (broken.size) {
  console.error(`check-links: ${broken.size} broken internal link(s) across ${html.length} pages\n`);
  for (const [link, from] of broken) {
    const where = [...from];
    console.error(`  ${link}\n    on ${where.slice(0, 3).join(', ')}${where.length > 3 ? ` (+${where.length - 3} more)` : ''}`);
  }
  process.exit(1);
}
console.log(`check-links: ${html.length} pages, no broken internal links`);
