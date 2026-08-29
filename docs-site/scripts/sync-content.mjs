// Generates every page of the docs site (except index.mdx) from the repository.
//
//   README.md               -> guide/*  and contributing/*   (split on ## / ###)
//   samples/*.nb.md         -> samples/*
//   docs/studio.md, docker  -> studio/*
//   docs/images/*           -> public/images/*
//   src/*/*.csproj          -> reference/packages.md  (PackageId + Description)
//   Directory.Build.props   -> src/data/version.json
//   scripts/out/cli/*.txt   -> reference/cli.md       (written by gen-cli-reference.sh)
//   api/*.md (from docfx)   -> api/*  gets the frontmatter Starlight requires
//
// Run from docs-site/:  node scripts/sync-content.mjs
// Idempotent: generated directories are wiped and rewritten every run. Edit the
// sources in the repo, never the output.

import { promises as fs } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const site = path.resolve(here, '..');
const repo = path.resolve(site, '..');
const docs = path.join(site, 'src', 'content', 'docs');
const base = process.env.DOCS_BASE ?? '/ClrKernel';
const ghBlob = 'https://github.com/ClrKernel/ClrKernel/blob/main/';
const ghTree = 'https://github.com/ClrKernel/ClrKernel/tree/main/';

// ---------- helpers ----------

const kebab = (s) =>
  s
    .toLowerCase()
    .replace(/[`*_]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');

// GitHub's heading -> anchor rule (close enough to Starlight's github-slugger).
const anchor = (s) =>
  s
    .toLowerCase()
    .replace(/[`*_]/g, '')
    .replace(/[^\w\- ]+/g, '')
    .trim()
    .replace(/\s+/g, '-');

// A double-quoted YAML scalar interprets backslashes, so a title carrying one
// ("Class Message<T\\>" — docfx escapes its generics) is a parse error, not a
// stray character. Escape the backslash before the quote.
const yaml = (s) => s.replace(/\\/g, '\\\\').replace(/"/g, '\\"');

const fm = (title, extra = {}) => {
  const lines = ['---', `title: "${yaml(title)}"`, 'editUrl: false'];
  for (const [k, v] of Object.entries(extra)) {
    if (v === undefined) continue;
    if (typeof v === 'object') {
      lines.push(`${k}:`);
      for (const [k2, v2] of Object.entries(v)) lines.push(`  ${k2}: ${JSON.stringify(v2)}`);
    } else lines.push(`${k}: ${JSON.stringify(v)}`);
  }
  lines.push('---', '');
  return lines.join('\n');
};

const sourceNote = (relPath, what = 'page') =>
  `:::note[Source]\nThis ${what} is generated from [\`${relPath}\`](${ghBlob}${relPath}) in the repository. To improve it, edit that file.\n:::\n\n`;

async function reset(dir) {
  await fs.rm(dir, { recursive: true, force: true });
  await fs.mkdir(dir, { recursive: true });
}

async function write(rel, content) {
  const p = path.join(docs, rel);
  await fs.mkdir(path.dirname(p), { recursive: true });
  await fs.writeFile(p, content);
}

// Where a repo-relative path lives on the site (or on GitHub if it has no page).
const pageUrl = {
  samples: (name) => `${base}/samples/${kebab(name.replace(/\.nb\.md$/, ''))}/`,
  studio: `${base}/studio/`,
  docker: `${base}/studio/docker/`,
};

function rewriteLinks(md, fromDir) {
  return md.replace(/\]\(([^)\s]+)\)/g, (m, href) => {
    if (href.startsWith(base + '/')) return m; // already a site URL
    if (/^(https?:|mailto:|#)/.test(href)) {
      // README uses one raw.githubusercontent image; bring it local.
      const raw = href.match(/^https:\/\/raw\.githubusercontent\.com\/ClrKernel\/ClrKernel\/main\/docs\/images\/(.+)$/);
      if (raw) return `](${base}/images/${raw[1]})`;
      const blob = href.startsWith(ghBlob) ? href.slice(ghBlob.length) : null;
      if (!blob) return m;
      href = blob;
    }
    // Resolve to repo-relative, keeping any fragment aside — `docker.md#passwords`
    // is still the docker page.
    const hash = href.includes('#') ? href.slice(href.indexOf('#')) : '';
    if (hash) href = href.slice(0, -hash.length);
    const rel = path.posix.normalize(path.posix.join(fromDir, href)).replace(/^\.\//, '');
    let s = rel.match(/^samples\/([^/]+\.nb\.md)$/);
    if (s) return `](${pageUrl.samples(s[1])}${hash})`;
    if (rel === 'docs/studio.md') return `](${pageUrl.studio}${hash})`;
    if (rel === 'docs/docker.md') return `](${pageUrl.docker}${hash})`;
    let img = rel.match(/^docs\/images\/(.+)$/);
    if (img) return `](${base}/images/${img[1]})`;
    // Directories and anything without a page: send to GitHub.
    const isDir = rel.endsWith('/') || !path.posix.extname(rel);
    return `](${isDir ? ghTree : ghBlob}${rel}${hash})`;
  });
}

// Shift headings so a page carved out of an H2/H3 starts at H2 (Starlight renders
// the frontmatter title as the H1).
function demote(md, topLevel) {
  const shift = topLevel - 1;
  return md.replace(/^(#{1,6})\s/gm, (m, h) => '#'.repeat(Math.max(2, h.length - shift)) + ' ');
}

// ---------- README -> guide/ + contributing/ ----------

// Nicer slugs and sidebar order than kebab(title) gives. Anything not listed
// falls back to kebab(title) and sorts alphabetically after these.
const readmePages = {
  Overview: { slug: 'overview', order: 0 },
  Install: { slug: 'install', order: 1 },
  Use: { slug: 'notebook-basics', order: 2, title: 'Notebook basics' },
  'Importing shared libraries': { slug: 'importing', order: 3 },
  'Shell & PowerShell cells — local and remote': { slug: 'shell-and-powershell', order: 4, title: 'Shell & PowerShell cells' },
  'SQL cells': { slug: 'sql', order: 5 },
  'Other databases (Oracle, ODBC, JDBC)': { slug: 'other-databases', order: 6 },
  'Analysis Services (SSAS / Fabric)': { slug: 'analysis-services', order: 7 },
  'Fabric warehouse writes': { slug: 'fabric-warehouse', order: 8 },
  'Headless execution': { slug: 'headless', order: 9 },
  'Scheduling notebooks — ClrKernel Studio (preview)': { slug: 'scheduling', order: 10, title: 'Scheduling with Studio' },
  'Build & test': { slug: 'build-and-test', order: 0, dir: 'contributing' },
  Develop: { slug: 'develop', order: 1, dir: 'contributing' },
};

async function syncReadme() {
  const text = await fs.readFile(path.join(repo, 'README.md'), 'utf8');
  const lines = text.split('\n');

  // Pass 1: cut into pages. H2 opens a page; inside "Use", each H3 opens a page.
  // Everything else (H4+, and H3s outside "Use") stays in its parent page.
  const pages = [];
  let cur = null;
  let inUse = false;
  let inFence = false;
  const open = (title, level) => {
    const cfg = readmePages[title] ?? {};
    cur = {
      title: cfg.title ?? title,
      slug: cfg.slug ?? kebab(title),
      dir: cfg.dir ?? 'guide',
      order: cfg.order,
      level,
      body: [],
      anchors: [],
    };
    cur.anchors.push(anchor(title));
    pages.push(cur);
  };
  for (const line of lines) {
    if (/^```/.test(line)) inFence = !inFence;
    const h = !inFence && line.match(/^(#{1,6})\s+(.*)$/);
    if (h) {
      const level = h[1].length;
      const title = h[2].trim();
      if (level === 1) { open('Overview', 1); continue; }
      if (level === 2) { inUse = title === 'Use'; if (title === 'License') { cur = null; continue; } open(title, 2); continue; }
      if (level === 3 && inUse) { open(title, 3); continue; }
      if (cur) { cur.anchors.push(anchor(title)); cur.body.push(line); }
      continue;
    }
    if (cur) cur.body.push(line);
  }

  // Pass 2: cross-page anchors. "[below](#sql-cells)" must become a link to the page
  // that now owns that heading.
  const anchorOwner = new Map();
  for (const p of pages) for (const a of p.anchors) if (!anchorOwner.has(a)) anchorOwner.set(a, p);

  for (const p of pages) {
    let body = p.body.join('\n').trim() + '\n';
    body = body.replace(/\]\(#([^)]+)\)/g, (m, a) => {
      const owner = anchorOwner.get(a);
      if (!owner || owner === p) return m;
      const url = `${base}/${owner.dir}/${owner.slug}/`;
      return owner.anchors[0] === a ? `](${url})` : `](${url}#${a})`;
    });
    body = rewriteLinks(body, '.');
    body = demote(body, p.level);
    const out = fm(p.title, { sidebar: p.order !== undefined ? { order: p.order } : undefined })
      + sourceNote('README.md', 'page')
      + body;
    await write(`${p.dir}/${p.slug}.md`, out);
  }
  return pages.length;
}

// ---------- samples/*.nb.md -> samples/ ----------

async function syncSamples() {
  const dir = path.join(repo, 'samples');
  const files = (await fs.readdir(dir)).filter((f) => f.endsWith('.nb.md')).sort();
  let order = 10;
  for (const f of files) {
    let md = await fs.readFile(path.join(dir, f), 'utf8');
    const h1 = md.match(/^#\s+(.+)$/m);
    const title = h1 ? h1[1].trim() : f.replace(/\.nb\.md$/, '');
    if (h1) md = md.replace(h1[0], '').trimStart();
    md = rewriteLinks(md, 'samples');
    md = demote(md, 1);
    const slug = kebab(f.replace(/\.nb\.md$/, ''));
    // hello.nb.md is the "what is executable markdown" intro; pin it first.
    const ord = f === 'hello.nb.md' ? 0 : order++;
    const note = `:::tip[This page is a runnable notebook]\nIt is [\`samples/${f}\`](${ghBlob}samples/${f}) in the repository — open it in VS Code with the ClrKernel Notebooks extension, or run it headlessly with \`clrkernel run ${f}\`. The documentation and the notebook are the same file.\n:::\n\n`;
    await write(`samples/${slug}.md`, fm(title, { sidebar: { order: ord } }) + note + md);
  }
  return files.length;
}

// ---------- docs/studio.md, docs/docker.md -> studio/ ----------

async function syncStudioDocs() {
  const map = [
    ['docs/studio.md', 'studio/index.md', 0],
    ['docs/docker.md', 'studio/docker.md', 1],
  ];
  for (const [src, dest, order] of map) {
    let md = await fs.readFile(path.join(repo, src), 'utf8');
    const h1 = md.match(/^#\s+(.+)$/m);
    const title = h1 ? h1[1].trim() : path.basename(src, '.md');
    if (h1) md = md.replace(h1[0], '').trimStart();
    md = rewriteLinks(md, 'docs');
    md = demote(md, 1);
    await write(dest, fm(title, { sidebar: { order } }) + sourceNote(src) + md);
  }
}

// ---------- docs/images -> public/images ----------

async function syncImages() {
  const src = path.join(repo, 'docs', 'images');
  const dest = path.join(site, 'public', 'images');
  await reset(dest);
  await fs.cp(src, dest, { recursive: true });
}

// ---------- version + packages -> reference/ ----------

async function syncVersion() {
  const props = await fs.readFile(path.join(repo, 'Directory.Build.props'), 'utf8');
  const version = props.match(/<Version>([^<]+)<\/Version>/)?.[1] ?? 'unknown';
  await fs.mkdir(path.join(site, 'src', 'data'), { recursive: true });
  await fs.writeFile(path.join(site, 'src', 'data', 'version.json'), JSON.stringify({ version }, null, 2) + '\n');
  return version;
}

async function syncPackages(version) {
  const srcDir = path.join(repo, 'src');
  const rows = [];
  for (const proj of (await fs.readdir(srcDir)).sort()) {
    const csproj = path.join(srcDir, proj, `${proj}.csproj`);
    let xml;
    try { xml = await fs.readFile(csproj, 'utf8'); } catch { continue; }
    const id = xml.match(/<PackageId>([^<]+)<\/PackageId>/)?.[1];
    if (!id) continue;
    const desc = (xml.match(/<Description>([\s\S]*?)<\/Description>/)?.[1] ?? '').replace(/\s+/g, ' ').trim();
    const tool = /<PackAsTool>true<\/PackAsTool>/i.test(xml);
    rows.push({ id, desc, tool, proj });
  }
  const table = (items) =>
    ['| Package | Description |', '| --- | --- |', ...items.map((r) => `| [\`${r.id}\`](https://www.nuget.org/packages/${r.id}) | ${r.desc.replace(/\|/g, '\\|')} |`)].join('\n');
  const tools = rows.filter((r) => r.tool);
  const libs = rows.filter((r) => !r.tool);
  const md =
    fm('NuGet packages', { sidebar: { order: 0 } }) +
    `All packages ship together at version **${version}**. Load a library into a notebook with \`#r "nuget: <Package>"\`; tools are installed with \`dotnet tool install --global <Package>\`.\n\n` +
    (tools.length ? `## dotnet tools\n\n${table(tools)}\n\n` : '') +
    `## Libraries\n\n${table(libs)}\n\n` +
    `:::note[Source]\nGenerated from the \`<PackageId>\` and \`<Description>\` of each project under [\`src/\`](${ghTree}src).\n:::\n`;
  await write('reference/packages.md', md);
  return rows.length;
}

// ---------- CLI help captures -> reference/cli.md ----------

async function syncCli() {
  const dir = path.join(site, 'scripts', 'out', 'cli');
  let files;
  // Sort by the command the file stands for, so `clrkernel` leads and its
  // subcommand follows it; by file name, `clrkernel-studio.txt` would come first.
  const cmdOf = (f) => f.replace(/\.txt$/, '').replace(/__/g, ' ');
  try {
    files = (await fs.readdir(dir)).filter((f) => f.endsWith('.txt'))
      .sort((a, b) => cmdOf(a).localeCompare(cmdOf(b)));
  } catch { return 0; }
  if (!files.length) return 0;
  let md = fm('Command line', { sidebar: { order: 1 } }) +
    `Captured from \`--help\` at build time, so it always matches the version this site documents.\n\n`;
  const seen = new Set();
  for (const f of files) {
    const txt = (await fs.readFile(path.join(dir, f), 'utf8')).trimEnd();
    if (!txt) continue;
    // Several commands answer --help with the same top-level usage; print it once.
    if (seen.has(txt)) continue;
    seen.add(txt);
    const cmd = f.replace(/\.txt$/, '').replace(/__/g, ' ');
    md += `## \`${cmd} --help\`\n\n\`\`\`text\n${txt}\n\`\`\`\n\n`;
  }
  await write('reference/cli.md', md);
  return files.length;
}

// ---------- docfx markdown -> api/ (frontmatter only) ----------

// "ClrKernel.Database.SqlServer.md" would slug to one unreadable word; hyphens
// give /api/clrkernel-database-sqlserver/ instead.
const apiSlug = (name) => name.replace(/\.md$/, '').replace(/\./g, '-').toLowerCase();

async function fixApi() {
  const dir = path.join(docs, 'api');
  let files;
  try { files = await fs.readdir(dir); } catch { files = []; }
  // docfx cross-references its own output by file name — `](ClrKernel.Core.Scripting.md)`.
  // Those are relative .md links to files this function is about to rename, so every
  // one of them 404s unless it is pointed at the page the file becomes. Collect the
  // names first; a link to something docfx did not emit is left alone rather than
  // turned into a link to nowhere.
  const known = new Set(files.filter((f) => f.endsWith('.md')));
  const linkToPage = (md) =>
    md.replace(/\]\(([^)\s]+)\)/g, (m, dest) => {
      // docfx escapes the `#` and the `_`s in its own hrefs; undo that before matching.
      const [file, hash] = dest.replace(/\\(.)/g, '$1').split('#');
      if (!known.has(file)) return m;
      return `](${base}/api/${apiSlug(file)}/${hash ? '#' + hash : ''})`;
    });

  let n = 0;
  for (const f of files) {
    const p = path.join(dir, f);
    if (!f.endsWith('.md')) { await fs.rm(p, { force: true }); continue; } // toc.yml etc.
    // Rename before reading. On a case-insensitive filesystem `ClrKernel.md` and
    // `clrkernel.md` are one file, so writing the second and deleting the first
    // deletes the page that was just written — and the namespace root is exactly
    // the name that differs from its slug only by case.
    const renamed = path.join(dir, apiSlug(f) + '.md');
    if (renamed !== p) await fs.rename(p, renamed);
    let md = await fs.readFile(renamed, 'utf8');
    if (md.startsWith('---\n')) { n++; continue; }
    const h1 = md.match(/^#\s+(.+)$/m);
    // docfx writes `# <a id="Fully_Qualified"></a> Class Message<T\>` — the anchor
    // is for its own theme, and the backslashes are its markdown escaping. Both
    // would end up in the sidebar and the <title> verbatim.
    let title = h1 ? h1[1].replace(/<a\s[^>]*><\/a>/g, '').trim() : f.replace(/\.md$/, '');
    title = title.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/\\(.)/g, '$1');
    title = title.replace(/^(Namespace|Class|Struct|Interface|Enum|Delegate)\s+/, '');
    if (h1) md = md.replace(h1[0], '').trimStart();
    // Unescape docfx's HTML-escaped generics in headings/links so they read as C#.
    md = md.replace(/&lt;/g, '<').replace(/&gt;/g, '>');
    md = linkToPage(md);
    await fs.writeFile(renamed, fm(title) + md);
    n++;
  }
  if (!n) {
    await write('api/index.md', fm('API reference') +
      'The API reference is generated by `npm run api` (DocFX) from the XML doc comments in `src/`. It was not generated for this build.\n');
  }
  return n;
}

// ---------- main ----------

for (const d of ['guide', 'samples', 'studio', 'contributing', 'reference']) await reset(path.join(docs, d));
const version = await syncVersion();
const n = {
  readme: await syncReadme(),
  samples: await syncSamples(),
  packages: await syncPackages(version),
  cli: await syncCli(),
  api: await fixApi(),
};
await syncStudioDocs();
await syncImages();
console.log(`sync-content: v${version} — ${n.readme} README pages, ${n.samples} samples, ${n.packages} packages, ${n.cli} CLI captures, ${n.api} API pages`);
