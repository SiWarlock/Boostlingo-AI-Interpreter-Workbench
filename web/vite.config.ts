import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// Vitest config lives here via the `test` block (vitest/config re-exports Vite's defineConfig
// with the `test` field typed). jsdom + Testing Library are intentionally deferred to D.7,
// when real component tests land — the A.1 smoke test runs in the default node environment.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'node',
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
})
