import { AlertTriangle } from 'lucide-react'
import { useSessionState } from '../state/sessionStore'
import { errorCopy } from './errorCopy'

// Surfaces the store's sanitized UiError[] as actionable copy (ARCH-007 / ARCH-018). Renders ONLY from
// the store (clean separation) and maps each error through errorCopy — never raw provider text. Render-
// only for D.7 (no per-error dismiss; clearErrors fires on new-session). role="alert" announces the
// surfaced error to assistive tech. Returns null when there are no errors (no empty banner).
//
// H.1 styling: each error renders as an inline .toast.err strip (Q3=b — inline, not a fixed toast, no
// dismiss). CSS/markup only — role="alert", the aria-label, and the errorCopy text are unchanged.
export default function ErrorBanner() {
  const { errors } = useSessionState()
  if (errors.length === 0) {
    return null
  }
  return (
    <section className="error-banner" aria-label="error-banner" role="alert">
      {errors.map((error, i) => (
        <div key={`${error.code}-${i}`} className="toast err">
          <span className="tic">
            <AlertTriangle size={13} aria-hidden />
          </span>
          <div style={{ flex: 1 }} className="tm">
            {errorCopy(error)}
          </div>
        </div>
      ))}
    </section>
  )
}
