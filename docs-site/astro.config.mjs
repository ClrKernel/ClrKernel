// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

// GitHub Pages serves a project site at https://<org>.github.io/<repo>/, so the
// site needs a base path. DOCS_BASE lets `npm run dev` and a fork's Pages both work.
const base = process.env.DOCS_BASE ?? '/ClrKernel';

export default defineConfig({
  site: process.env.DOCS_SITE ?? 'https://clrkernel.github.io',
  base,
  integrations: [
    starlight({
      title: 'ClrKernel',
      description:
        'Notebooks as plain markdown. C#, SQL, DAX, PowerShell, shell, HTTP and Mermaid cells in one session — in VS Code, ClrKernel Studio, JupyterLab, or headless.',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/ClrKernel/ClrKernel' },
      ],
      editLink: {
        // Pages are generated; "Edit page" is disabled per-page via frontmatter so
        // the link points at the real source (README, samples/, docs/) instead.
        baseUrl: 'https://github.com/ClrKernel/ClrKernel/edit/main/',
      },
      sidebar: [
        { label: 'Guide', items: [{ autogenerate: { directory: 'guide' } }] },
        { label: 'VS Code', items: [{ autogenerate: { directory: 'vscode' } }] },
        { label: 'Samples (runnable notebooks)', items: [{ autogenerate: { directory: 'samples' } }] },
        { label: 'Studio', items: [{ autogenerate: { directory: 'studio' } }] },
        { label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
        { label: 'API', collapsed: true, items: [{ autogenerate: { directory: 'api' } }] },
        { label: 'Contributing', items: [{ autogenerate: { directory: 'contributing' } }] },
      ],
      lastUpdated: true,
      customCss: ['./src/styles/custom.css'],
      head: [
        {
          // Click a screenshot to see it full size. Native <dialog>; styled in custom.css.
          tag: 'script',
          content: `document.addEventListener('click', e => {
  const img = e.target.closest('.sl-markdown-content img');
  if (!img || img.closest('a')) return;
  let dlg = document.getElementById('img-lightbox');
  if (!dlg) {
    dlg = document.createElement('dialog');
    dlg.id = 'img-lightbox';
    dlg.appendChild(document.createElement('img'));
    dlg.addEventListener('click', () => dlg.close());
    document.body.appendChild(dlg);
  }
  const big = dlg.firstChild;
  big.src = img.currentSrc || img.src;
  big.alt = img.alt;
  dlg.showModal();
});`,
        },
      ],
    }),
  ],
});
