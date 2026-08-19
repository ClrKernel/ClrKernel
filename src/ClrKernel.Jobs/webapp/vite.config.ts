import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Build output goes straight into the project's wwwroot, which the tool serves
// (and packs). `npm run dev` proxies /api to a locally running `clrkernel-jobs serve`.
export default defineConfig({
  plugins: [react()],
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
