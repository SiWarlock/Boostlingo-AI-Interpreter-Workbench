import { useCallback, useEffect, useState } from 'react'
import { History, RefreshCw } from 'lucide-react'
import { sessionsApi } from '../api/sessionsApi'
import { loadHistory } from '../state/historyActions'
import { sessionStore } from '../state/sessionStore'
import type { SessionListItem } from '../types/domain'

// Session-history panel (H.3-frontend, ARCH-007 / ARCH-017). A read-only browse of the persisted sessions
// (GET /api/sessions, 065) so past evidence runs are visible even across a server restart. Renders from
// TRANSIENT local state (not UiSessionState — §20 precedent); dispatches the DI'd loadHistory flow (errors
// route to the store sink → the global ErrorBanner, §2 — no panel-local error state). List-only this slice;
// per-session drill-in pairs with the GET /{id} disk-read fallback (a BE follow-up). The backend orders
// most-recent-first → the view does NOT re-sort (§35).

// Readable wall-clock for a wire ISO timestamp (locale default; the year is timezone-stable for the tests).
function formatWhen(iso: string): string {
  return new Date(iso).toLocaleString()
}

export default function SessionHistory() {
  const [items, setItems] = useState<SessionListItem[]>([])
  const [loading, setLoading] = useState(false)

  const refresh = useCallback(async (isCancelled: () => boolean = () => false): Promise<void> => {
    setLoading(true)
    try {
      const list = await loadHistory({ store: sessionStore, api: sessionsApi })
      // null = a fetch failure already routed to the store sink (ErrorBanner) — keep the prior list.
      if (!isCancelled() && list) setItems(list)
    } finally {
      if (!isCancelled()) setLoading(false)
    }
  }, [])

  // Fetch on mount with an effect-scoped cancelled guard (mirrors the EvaluationPanel mount fetch): a
  // StrictMode double-invoke or an unmount mid-flight must not setState on a stale/dead instance. The list
  // goes stale as new sessions persist → a manual Refresh re-fetches (unguarded, like a button handler).
  useEffect(() => {
    let cancelled = false
    void refresh(() => cancelled)
    return () => {
      cancelled = true
    }
  }, [refresh])

  return (
    <section className="card card-pad" aria-label="session-history">
      <div className="card-hd">
        <span className="ic">
          <History size={18} aria-hidden />
        </span>
        <span className="card-title">Session history</span>
        <span className="right">
          <button
            type="button"
            className="btn btn-outline"
            onClick={() => void refresh()}
            disabled={loading}
          >
            <span className="ic">
              <RefreshCw size={15} aria-hidden />
            </span>
            Refresh
          </button>
        </span>
      </div>

      {items.length === 0 ? (
        <p className="bl-sm" style={{ margin: 0 }}>
          No past sessions yet.
        </p>
      ) : (
        <ul aria-label="history-list" className="hist-list">
          {items.map((item) => (
            <li key={item.sessionId} className="hist-row">
              <div className="hist-main">
                <span className="hist-title">{item.label ?? item.sessionId}</span>
                <span className="hist-when bl-sm">
                  {formatWhen(item.startedAt)}
                  {item.endedAt ? ` → ${formatWhen(item.endedAt)}` : ' · in progress'}
                </span>
              </div>
              <div className="hist-meta">
                <span className="hist-turns bl-sm">
                  {item.turnCount} {item.turnCount === 1 ? 'turn' : 'turns'}
                </span>
                <span className="hist-modes">
                  {item.modes.map((mode) => (
                    <span className="chip" key={mode}>
                      <span
                        className="d"
                        style={{
                          background: mode === 'realtime' ? 'var(--bl-blue)' : 'var(--bl-violet)',
                        }}
                      />
                      {mode}
                    </span>
                  ))}
                </span>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
