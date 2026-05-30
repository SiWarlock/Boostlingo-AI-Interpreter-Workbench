// Global Vitest setup. Registers @testing-library/jest-dom matchers (toBeInTheDocument, toBeDisabled,
// …) on Vitest's expect. This is DOM-free at import time — it only augments the matcher registry — so
// it is safe under the default `environment: 'node'` (the 92 logic tests). The jsdom realm + RTL render
// are scoped per-file via `// @vitest-environment jsdom` (the 2 component tests); those files do their
// own `afterEach(cleanup)`, so node-env files never import @testing-library/react. (D.7)
import '@testing-library/jest-dom/vitest'
