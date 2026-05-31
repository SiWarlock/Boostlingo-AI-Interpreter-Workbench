/* Stage + Metrics + Cost + Comparison + Evaluation + Error toasts */
const { fmtLatency: fL, fmtCostMin, fmtCostTurn, latClass, werClass, TARGET } = window.WB;
const Icon2 = window.Icon;
const { Card: C, CardHead: CH, Button: Btn, StatusPill: SP, Eyebrow } = window;

/* value-or-n/a span */
function Val({ v, cls }) {
  if (v == null) return <span className="na">n/a</span>;
  return <span className={cls || ''}>{v}</span>;
}

/* ---------- Transcript panel ---------- */
function TranscriptPanel({ s }) {
  const t = s.currentTurn;
  const dir = (t?.direction || s.direction);
  const render = (arr, accent) => {
    if (!arr || arr.length === 0) return null;
    return arr.map((seg, i) => (
      <div key={i} className={'tx-line ' + (seg.isFinal ? '' : 'partial')}>
        {seg.text}
        {!seg.isFinal && <span className="cursor" style={{ background: accent }} />}
      </div>
    ));
  };
  const hasContent = t && (t.sourceTranscript?.length || t.targetTranscript?.length);
  return (
    <C className="card-pad" style={{ minHeight: 360 }}>
      <CH icon="activity" title="Live transcript"
        right={t ? <SP value={s.turnStatus} /> : null} />
      {!hasContent ? (
        <div className="tx-empty">
          <Icon2 name="mic" size={26} stroke={1.5} />
          <div>No turn yet — press <b>Start recording</b> to begin.</div>
        </div>
      ) : (
        <div className="tx-cols">
          <div className="tx-col">
            <div className="tx-hd"><span className="eyebrow">Source</span><span className="tx-flag">{dir.source.toUpperCase()}</span></div>
            {render(t.sourceTranscript, 'var(--bl-blue)') || <div className="tx-line partial">listening…<span className="cursor" style={{ background: 'var(--bl-blue)' }} /></div>}
          </div>
          <div className="tx-col">
            <div className="tx-hd"><span className="eyebrow">Target</span><span className="tx-flag">{dir.target.toUpperCase()}</span></div>
            {render(t.targetTranscript, 'var(--bl-violet)') || <div className="tx-line partial" style={{ color: 'var(--fg-3)' }}>—</div>}
          </div>
        </div>
      )}
    </C>
  );
}

/* ---------- Metrics panel ---------- */
function MetricsPanel({ s, actions }) {
  const t = s.currentTurn;
  const mode = t?.mode || s.mode;
  const lat = t?.latency || {};
  const headlineMs = mode === 'realtime' ? lat.speechEndToFirstAudioMs : lat.totalTurnMs;
  const cls = latClass(mode, headlineMs);
  const tgt = TARGET[mode] / 1000;
  const stages = lat.stages || {};
  const stageList = [
    { k: 'stt', label: 'STT', color: 'var(--bl-blue)', v: stages.stt },
    { k: 'translation', label: 'Translation', color: 'var(--bl-violet)', v: stages.translation },
    { k: 'tts', label: 'TTS', color: 'var(--bl-coral)', v: stages.tts },
  ];
  const totalStage = stageList.reduce((a, x) => a + (x.v || 0), 0) || 1;
  const sum = s.summary?.byMode?.[mode];
  const tgtCls = cls === 'good' ? 'tgt-good' : cls === 'warn' ? 'tgt-warn' : cls === 'over' ? 'tgt-over' : '';

  return (
    <C className="card-pad">
      <CH icon="gauge" title="Metrics"
        right={<Btn variant="ghost" sm icon="refresh" onClick={() => actions && actions.loadScenario('comparison')}>Refresh</Btn>} />
      <Eyebrow>{mode === 'realtime' ? 'This turn · speech → first audio' : 'This turn · total turn'}</Eyebrow>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 10, marginTop: 8, flexWrap: 'wrap' }}>
        <span className={'metric-big ' + (cls === 'good' ? 'good' : cls === 'over' ? 'over' : cls === 'warn' ? 'warn' : '')}>
          {headlineMs != null ? fL(headlineMs) : <span className="na">n/a</span>}
        </span>
        {headlineMs != null && <span className={'tgt-pill ' + tgtCls}>target &lt; {tgt}s</span>}
      </div>

      {mode === 'cascade' && (
        <div style={{ marginTop: 16 }}>
          <Eyebrow>Per-stage</Eyebrow>
          <div className="stage-bar">
            {stageList.map(st => st.v ? <div key={st.k} className="seg-fill" style={{ width: (st.v / totalStage * 100) + '%', background: st.color }} /> : null)}
          </div>
          <div className="stage-legend">
            {stageList.map(st => (
              <span key={st.k} className="lg"><span className="k" style={{ background: st.color }} />{st.label} {st.v != null ? Math.round(st.v) + 'ms' : <span className="na">n/a</span>}</span>
            ))}
          </div>
        </div>
      )}

      <div className="divider" />
      <Eyebrow>Session averages · {mode}</Eyebrow>
      <div style={{ marginTop: 6 }}>
        <div className="kv"><span className="k">Avg {mode === 'realtime' ? 'first-audio' : 'total'}</span><span className="v"><Val v={sum?.avgLatencyMs != null ? fL(sum.avgLatencyMs) : null} /></span></div>
        <div className="kv"><span className="k">Avg cost</span><span className="v"><Val v={sum?.avgCostMin != null ? fmtCostMin(sum.avgCostMin) + ' /min' : null} /></span></div>
        <div className="kv"><span className="k">Turns</span><span className="v">{sum?.turnCount ?? 0}</span></div>
      </div>
    </C>
  );
}

/* ---------- Cost panel ---------- */
function CostPanel({ s }) {
  const t = s.currentTurn;
  const cost = t?.cost;
  const perMin = t?.estimatedCostPerMinuteUsd;
  return (
    <C className="card-pad">
      <CH icon="dollar" title="Cost" />
      <Eyebrow>Estimated rate</Eyebrow>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginTop: 8 }}>
        <span style={{ fontSize: 12, color: 'var(--fg-2)', background: 'var(--surface-sunken)', padding: '3px 8px', borderRadius: 6 }}>Estimated</span>
        <span className="metric-big" style={{ fontSize: 34 }}>{perMin != null ? '$' + perMin.toFixed(2) : <span className="na">n/a</span>}</span>
        <span className="metric-unit">/ min</span>
      </div>
      <div style={{ marginTop: 12 }}>
        <div className="kv"><span className="k">Model</span><span className="v">{cost?.model || <span className="na">n/a</span>}</span></div>
        <div className="kv"><span className="k">This turn</span><span className="v"><Val v={t?.estimatedCostUsd != null ? fmtCostTurn(t.estimatedCostUsd) : null} /></span></div>
        <div className="kv"><span className="k">Output pricing</span><span className="v na">n/a</span></div>
      </div>
      {cost?.assumption &&
        <div style={{ marginTop: 12, display: 'flex', gap: 6, alignItems: 'flex-start', fontSize: 11.5, color: 'var(--fg-3)' }}>
          <Icon2 name="info" size={13} style={{ marginTop: 1, flex: 'none' }} />
          <span>Assumes {cost.assumption}.</span>
        </div>}
    </C>
  );
}

/* ---------- Comparison summary ---------- */
function ComparisonSummary({ s }) {
  const sum = s.summary;
  const hasData = sum && (sum.byMode.realtime.turnCount || sum.byMode.cascade.turnCount);
  const modeRow = (mode, color, label) => {
    const m = sum.byMode[mode];
    const cls = latClass(mode, m.avgLatencyMs);
    return (
      <tr className="mode-row" key={mode}>
        <td><span className="cmp-mode"><span className="k" style={{ background: color }} />{label}</span></td>
        <td className={cls !== 'na' ? cls : ''}>{m.avgLatencyMs != null ? fL(m.avgLatencyMs) : <span className="na">n/a</span>}</td>
        <td>{m.avgCostMin != null ? fmtCostMin(m.avgCostMin) : <span className="na">n/a</span>}</td>
        <td>{mode === 'realtime' && sum.wer == null ? <span className="na">n/a</span> : (sum.wer ? sum.wer.avgWer + '%' : <span className="na">n/a</span>)}</td>
        <td>{m.errorCount}</td>
        <td>{m.turnCount}</td>
      </tr>
    );
  };
  return (
    <div className="cmp-card">
      <div className="card-pad" style={{ paddingBottom: 4 }}>
        <CH icon="columns" title="Comparison summary"
          right={<span className="eyebrow">Realtime vs Cascade · by mode &amp; variant</span>} />
      </div>
      {!hasData ? (
        <div style={{ padding: '8px 22px 28px', color: 'var(--fg-3)', fontSize: 14, textAlign: 'center' }}>
          Run turns in both modes to populate the head-to-head comparison.
        </div>
      ) : (
        <table className="cmp-table">
          <thead><tr><th>Mode / variant</th><th>Avg latency</th><th>Cost / min</th><th>WER</th><th>Errors</th><th>Turns</th></tr></thead>
          <tbody>
            {modeRow('realtime', 'var(--bl-blue)', 'Realtime')}
            {sum.variants.filter(v => v.mode === 'realtime').map(v => <VariantRow key={v.key} v={v} />)}
            {modeRow('cascade', 'var(--bl-violet)', 'Cascade')}
            {sum.variants.filter(v => v.mode === 'cascade').map(v => <VariantRow key={v.key} v={v} />)}
          </tbody>
        </table>
      )}
    </div>
  );
}
function VariantRow({ v }) {
  const cls = latClass(v.mode, v.avgLatencyMs);
  return (
    <tr className="variant">
      <td>{v.key}</td>
      <td className={cls !== 'na' ? cls : ''}>{v.avgLatencyMs != null ? fL(v.avgLatencyMs) : <span className="na">n/a</span>}</td>
      <td>{v.avgCostMin != null ? fmtCostMin(v.avgCostMin) : <span className="na">n/a</span>}</td>
      <td><span className="na">n/a</span></td>
      <td>{v.err}</td>
      <td>{v.turns}</td>
    </tr>
  );
}

/* ---------- Evaluation (WER) ---------- */
function EvaluationPanel({ actions, werResult, werRunning }) {
  const { EVAL_PHRASES } = window.WB;
  const [idx, setIdx] = React.useState(0);
  const wcls = werResult ? werClass(werResult.wer) : 'na';
  return (
    <C className="card-pad">
      <CH icon="target" title="Evaluation · WER"
        right={<span className="eyebrow">STT accuracy only</span>} />
      <div className="eval-row">
        <div className="eval-ref">
          <span className="field-lab">Scripted phrase</span>
          <select className="select" value={idx} style={{ marginTop: 6 }} onChange={e => setIdx(+e.target.value)} disabled={werRunning}>
            {EVAL_PHRASES.map((p, i) => <option key={i} value={i}>Phrase {i + 1}</option>)}
          </select>
          <div className="txt">{EVAL_PHRASES[idx]}</div>
        </div>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12, alignItems: 'center', minWidth: 170 }}>
          <Btn variant="primary" icon={werRunning ? null : 'mic'} disabled={werRunning} onClick={() => actions.runWerEval(idx)}>
            {werRunning ? <><span className="spin" style={{ marginRight: 8 }} />Scoring…</> : 'Record & evaluate'}
          </Btn>
          <div className="wer-score">
            <div className={'metric-big ' + (wcls === 'good' ? 'good' : wcls === 'over' ? 'over' : wcls === 'warn' ? 'warn' : '')} style={{ fontSize: 40 }}>
              {werResult ? werResult.wer + '%' : <span className="na" style={{ fontSize: 40 }}>n/a</span>}
            </div>
            <div className="eyebrow" style={{ marginTop: 2 }}>Word error rate</div>
            {werResult &&
              <div className="sid"><span className="s">S {werResult.s}</span><span className="s">I {werResult.i}</span><span className="s">D {werResult.d}</span></div>}
          </div>
        </div>
      </div>
      <div style={{ marginTop: 12, display: 'flex', gap: 6, alignItems: 'center', fontSize: 11.5, color: 'var(--fg-3)' }}>
        <Icon2 name="info" size={13} /> WER measures STT accuracy only — not translation quality.
      </div>
    </C>
  );
}

/* ---------- Error toasts ---------- */
function ErrorToasts({ s, actions }) {
  if (!s.errors || s.errors.length === 0) return null;
  const TITLES = { mic_denied: 'Microphone blocked', tts_failed: 'Translation provider failed', net: 'Connection issue' };
  const ACT = { mic_denied: 'Retry', tts_failed: 'Switch to Realtime' };
  return (
    <div className="toast-wrap">
      {s.errors.map(e => (
        <div key={e.code} className={'toast ' + (e.code === 'mic_denied' ? 'err' : 'warn')}>
          <span className="tic"><Icon2 name="alert" size={13} /></span>
          <div style={{ flex: 1 }}>
            <div className="tt">{TITLES[e.code] || e.code}</div>
            <div className="tm">{e.safeMessage}
              {e.retryable && <button className="link-btn" onClick={() => actions.dismissError(e.code)}>{ACT[e.code] || 'Retry'}</button>}
            </div>
          </div>
          <button className="tx" onClick={() => actions.dismissError(e.code)}><Icon2 name="x" size={15} /></button>
        </div>
      ))}
    </div>
  );
}

Object.assign(window, { Val, TranscriptPanel, MetricsPanel, CostPanel, ComparisonSummary, EvaluationPanel, ErrorToasts });
