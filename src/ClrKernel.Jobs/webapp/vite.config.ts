import { createRequire } from 'node:module';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Monaco's package exports reject a subpath carrying Vite's `?worker` query, and
// `new URL(…, import.meta.url)` only understands relative paths — so resolve the
// worker with Node's own resolver here and give it a plain alias the app can
// import. Keeps the worker a local build asset (no CDN, works offline and in
// Docker) without hard-coding a node_modules layout.
const monacoEditorWorker = createRequire(import.meta.url).resolve(
  'monaco-editor/editor/editor.worker.js',
);

// Build output goes straight into the project's wwwroot, which the tool serves
// (and packs). `npm run dev` proxies /api to a locally running `clrkernel-jobs serve`.
export default defineConfig({
  plugins: [react()],
  resolve: {
    // The query is part of the id Vite matches, so it is part of the alias too.
    alias: { 'monaco-editor-worker?worker': `${monacoEditorWorker}?worker` },
  },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    // Points at a locally running `clrkernel-jobs serve`. Override when it is on
    // another port: CLRKERNEL_JOBS_API=http://localhost:5099 npm run dev
    proxy: {
      '/api': process.env.CLRKERNEL_JOBS_API ?? 'http://localhost:5000',
    },
  },
});
