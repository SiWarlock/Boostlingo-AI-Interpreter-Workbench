import { describe, expect, it } from 'vitest'
import App from './App'

// Harness-proof only (A.1): confirms Vitest discovers + runs a green target and that the
// TS/TSX transform pipeline works. Real component tests (render via Testing Library) land in D.7.
describe('scaffold', () => {
  it('runs the Vitest harness', () => {
    expect(1 + 1).toBe(2)
  })

  it('imports the App component', () => {
    expect(typeof App).toBe('function')
  })
})
