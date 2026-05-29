import type { UiError } from '../types/domain'

// The single failure boundary for every API client: a non-OK status AND a fetch rejection both
// surface as an ApiError carrying a sanitized UiError. The helper NEVER leaks a raw response body
// or a raw fetch/TypeError into the surfaced message (frontend error philosophy, ARCH-007/018/019),
// and NEVER propagates a non-ApiError — so callers can rely on `catch (e) { store.addError(e.uiError) }`.
export class ApiError extends Error {
  readonly uiError: UiError

  constructor(uiError: UiError) {
    super(uiError.safeMessage)
    this.name = 'ApiError'
    this.uiError = uiError
  }
}

// Read per-call (not at module-eval) so the base is overridable + test-stubbable. Empty base => the
// path is relative, which the Vite dev proxy serves (ARCH-029 proxy-or-direct; clients stay base-agnostic).
function baseUrl(): string {
  return import.meta.env.VITE_API_BASE_URL ?? ''
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await fetch(`${baseUrl()}${path}`, init)
  } catch {
    // fetch rejects (network down / backend not running) — synthesize, never propagate the TypeError.
    throw new ApiError({
      code: 'network.error',
      safeMessage: 'Could not reach the server. Check that the backend is running and retry.',
      retryable: true,
    })
  }

  if (!response.ok) {
    throw new ApiError(await toUiError(response))
  }

  // Success path honors the same boundary: an unparseable/empty 2xx body becomes an ApiError, never
  // a raw SyntaxError. (No D.1 endpoint returns non-JSON on 2xx, but the boundary stays complete.)
  try {
    return (await response.json()) as T
  } catch {
    throw new ApiError({
      code: 'response.invalid',
      safeMessage: 'The server returned an unexpected response.',
      retryable: false,
    })
  }
}

async function toUiError(response: Response): Promise<UiError> {
  let body: unknown
  try {
    body = await response.json()
  } catch {
    body = undefined
  }

  // A real sanitized UiError body (B.9 routed-path errors) is surfaced verbatim.
  if (isUiError(body)) {
    return body
  }

  // Anything else (ProblemDetails for framework 400 / unrouted 404-405, or non-JSON) — synthesize a
  // generic UiError keyed by status. The raw body is deliberately NOT included (no-leak hygiene).
  return {
    code: `http.${response.status}`,
    safeMessage: genericMessageFor(response.status),
    retryable: response.status === 429 || response.status >= 500,
  }
}

function isUiError(body: unknown): body is UiError {
  if (typeof body !== 'object' || body === null) {
    return false
  }
  const candidate = body as Record<string, unknown>
  return (
    typeof candidate.code === 'string' &&
    typeof candidate.safeMessage === 'string' &&
    typeof candidate.retryable === 'boolean'
  )
}

function genericMessageFor(status: number): string {
  if (status === 400) return 'The request was invalid.'
  if (status === 404) return 'The requested resource was not found.'
  if (status === 429) return 'Too many requests. Please wait a moment and retry.'
  if (status >= 500) return 'The server encountered an error. Please try again.'
  return 'The request failed.'
}
