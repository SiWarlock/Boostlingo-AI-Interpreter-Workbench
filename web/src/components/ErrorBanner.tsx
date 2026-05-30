import { useSessionState } from '../state/sessionStore'
import { errorCopy } from './errorCopy'

// Surfaces the store's sanitized UiError[] as actionable copy (ARCH-007 / ARCH-018). Renders ONLY from
// the store (clean separation) and maps each error through errorCopy — never raw provider text. Render-
// only for D.7 (no per-error dismiss; clearErrors fires on new-session). role="alert" announces the
// surfaced error to assistive tech. Returns null when there are no errors (no empty banner).
export default function ErrorBanner() {
  const { errors } = useSessionState()
  if (errors.length === 0) {
    return null
  }
  return (
    <section aria-label="error-banner" role="alert">
      <ul>
        {errors.map((error, i) => (
          <li key={`${error.code}-${i}`}>{errorCopy(error)}</li>
        ))}
      </ul>
    </section>
  )
}
