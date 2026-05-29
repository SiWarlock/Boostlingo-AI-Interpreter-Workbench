import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// Dev server + Vitest config. The `/api` proxy (ARCH-029) forwards REST + the cascade WS (`ws: true`)
// to the backend on :5179, so clients work zero-config with VITE_API_BASE_URL='' (relative paths).
// The browser Origin (http://localhost:5173) passes through unchanged, so the C.4b cascade-WS Origin
// check (FRONTEND_ORIGIN) is satisfied for both proxy and direct modes.
//
// Vitest config lives in the `test` block (vitest/config re-exports Vite's defineConfig with `test`
// typed). environment stays `node` — D.1 has only pure-logic tests; jsdom + Testing Library land in
// D.7 when real component render tests arrive.
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
  },
})
