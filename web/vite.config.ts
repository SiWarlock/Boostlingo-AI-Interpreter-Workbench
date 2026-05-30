import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// Dev server + Vitest config. The `/api` proxy (ARCH-029) forwards REST + the cascade WS (`ws: true`)
// to the backend on :5179, so clients work zero-config with VITE_API_BASE_URL='' (relative paths).
// The browser Origin (http://localhost:5173) passes through unchanged, so the C.4b cascade-WS Origin
// check (FRONTEND_ORIGIN) is satisfied for both proxy and direct modes.
//
// Vitest config lives in the `test` block (vitest/config re-exports Vite's defineConfig with `test`
// typed). The default environment stays `node` (the pure-logic suite); the D.7 component tests opt into
// jsdom per-file via `// @vitest-environment jsdom`, so the node-env tests (fetch/FormData/Blob as node
// globals) are untouched. `setupFiles` registers the jest-dom matchers globally (DOM-free at import).
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5179',
        ws: true,
      },
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
    setupFiles: ['src/test/setup.ts'],
  },
})
