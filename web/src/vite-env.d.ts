/// <reference types="vite/client" />

// Custom env vars (ARCH-029). VITE_API_BASE_URL is optional: empty/unset => relative paths served by
// the Vite dev proxy; a full origin enables direct mode against the backend's CORS-allowed origin.
interface ImportMetaEnv {
  readonly VITE_API_BASE_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
