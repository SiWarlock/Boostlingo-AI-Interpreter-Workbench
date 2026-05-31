import { useCallback, useEffect, useState } from 'react'
import { History, RefreshCw } from 'lucide-react'
import { sessionsApi } from '../api/sessionsApi'
import { loadHistory, loadSessionDetail } from '../state/historyActions'
import { sessionStore } from '../state/sessionStore'
import SessionDetail from './SessionDetail'
import type { InterpretationSession, SessionListItem } from '../types/domain'

// Session-history panel (H.3-frontend, ARCH-007 / ARCH-017). A read-only browse of the persisted sessions
// (GET /api/sessions, 065) so past evidence runs are visible even across a server restart. Renders from
// TRANSIENT local state (not UiSessionState — §20 precedent); dispatches the DI'd loadHistory/loadSessionDetail
// flows (errors route to the store sink → the global ErrorBanner, §2 — no panel-local error state). The
// backend orders most-recent-first → the view does NOT re-sort (§35). 071 adds a bounded-scroll container +
// a click-to-expand accordion: a row click fetches GET /{id} ONCE (068 disk-fallback) + caches it (a past
// session is immutable, Q2), single-open (Q1), rendering the SessionDetail drill-in inline below the row.

// Readable wall-clock for a wire ISO timestamp (locale default; the year is timezone-stable for the tests).
function formatWhen(iso: string): string {
  return new Date(iso).toLocaleString()
}

export default function SessionHistory() {
  const [items, setItems] = useState<SessionListItem[]>([])
  const [loading, setLoading] = useState(false)
  // Accordion state (071): single-open (Q1) + a per-id detail cache (Q2 — fetch once, a past session is
  // immutable). `failed` caches a detail-fetch failure so a re-expand shows the inline note without a refetch
  // storm (the sanitized error already went to the store sink → the ErrorBanner is the canonical surface, §2).
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [details, setDetails] = useState<Record<string, InterpretationSession>>({})
  const [failed, setFailed] = useState<Record<string, boolean>>({})

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

  // Toggle a row's accordion. Collapse if already open; else open (single-open) + fetch the detail ONCE
  // (cached on success OR failure — a past session is immutable; no refetch on re-expand).
  const toggle = useCallback(
    async (id: string): Promise<void> => {
      if (expandedId === id) {
        setExpandedId(null)
        return
      }
      setExpandedId(id)
      if (details[id] || failed[id]) return // served from cache — no refetch (Q2)
      const detail = await loadSessionDetail({ store: sessionStore, api: sessionsApi }, id)
      if (detail) {
        setDetails((prev) => ({ ...prev, [id]: detail }))
      } else {
        // loadSessionDetail already routed a sanitized UiError to the store sink (the banner); mark this id
        // so the row shows an inline "details unavailable" note instead of crashing / a blank expand.
        setFailed((prev) => ({ ...prev, [id]: true }))
      }
    },
    [expandedId, details, failed],
  )

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
        <div className="hist-scroll" data-testid="history-scroll">
          <ul aria-label="history-list" className="hist-list">
            {items.map((item) => {
              const expanded = expandedId === item.sessionId
              return (
                <li key={item.sessionId} className="hist-row-wrap">
                  <button
                    type="button"
                    className="hist-row hist-row-btn"
                    aria-expanded={expanded}
                    onClick={() => void toggle(item.sessionId)}
                  >
                    <span className="hist-main">
                      <span className="hist-title">{item.label ?? item.sessionId}</span>
                      <span className="hist-when bl-sm">
                        {formatWhen(item.startedAt)}
                        {item.endedAt ? ` → ${formatWhen(item.endedAt)}` : ' · in progress'}
                      </span>
                    </span>
                    <span className="hist-meta">
                      <span className="hist-turns bl-sm">
                        {item.turnCount} {item.turnCount === 1 ? 'turn' : 'turns'}
                      </span>
                      <span className="hist-modes">
                        {item.modes.map((mode) => (
                          <span className="chip" key={mode}>
                            <span
                              className="d"
                              style={{
                                background:
                                  mode === 'realtime' ? 'var(--bl-blue)' : 'var(--bl-violet)',
                              }}
                            />
                            {mode}
                          </span>
                        ))}
                      </span>
                    </span>
                  </button>

                  {expanded &&
                    (details[item.sessionId] ? (
                      <SessionDetail session={details[item.sessionId]} />
                    ) : failed[item.sessionId] ? (
                      <p className="bl-sm" style={{ margin: '8px 0 0' }}>
                        Details unavailable.
                      </p>
                    ) : (
                      <p className="bl-sm" style={{ margin: '8px 0 0' }}>
                        Loading…
                      </p>
                    ))}
                </li>
              )
            })}
          </ul>
        </div>
      )}
    </section>
  )
}
