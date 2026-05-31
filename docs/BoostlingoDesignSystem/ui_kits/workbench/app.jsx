/* App shell — composes the workbench, wires the store, adds a demo state switcher. */
const { Header: Hdr, ModeToggle: MT, SessionSetup: SS, RecordingControls: RC,
  TranscriptPanel: TP, MetricsPanel: MP, CostPanel: CP, ComparisonSummary: CS,
  EvaluationPanel: EP, ErrorToasts: ET, Button: B2 } = window;

function DemoBar({ actions, current, setCurrent }) {
  const items = [
    ['fresh', 'Idle'], ['recording', 'Recording'], ['streaming', 'Streaming'],
    ['completed', 'Completed'], ['comparison', 'Comparison'], ['error', 'Error'],
  ];
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
      margin: '0 0 18px', padding: '10px 14px', background: 'var(--surface)',
      border: '1px solid var(--border)', borderRadius: 14, boxShadow: 'var(--sh-xs)' }}>
      <span className="eyebrow" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <window.Icon name="play" size={13} /> Demo states
      </span>
      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
        {items.map(([k, label]) => (
          <button key={k}
            onClick={() => { actions.loadScenario(k); setCurrent(k); }}
            style={{
              fontFamily: 'var(--font-sans)', fontSize: 13, fontWeight: 600, cursor: 'pointer',
              padding: '7px 13px', borderRadius: 9, border: '1px solid',
              borderColor: current === k ? 'var(--bl-blue)' : 'var(--border)',
              background: current === k ? 'var(--bl-blue-tint)' : 'var(--surface)',
              color: current === k ? 'var(--bl-blue-600)' : 'var(--fg-2)',
            }}>{label}</button>
        ))}
      </div>
      <span style={{ marginLeft: 'auto', fontSize: 12, color: 'var(--fg-3)' }}>
        …or just press <b style={{ color: 'var(--fg-2)' }}>Start recording</b> to run a live turn
      </span>
    </div>
  );
}

function App() {
  const wb = window.useWorkbench();
  const { s, actions, werResult, werRunning } = wb;
  const [current, setCurrent] = React.useState('fresh');

  // any manual interaction clears the demo highlight
  const liveActions = React.useMemo(() => {
    const wrap = {};
    Object.keys(actions).forEach(k => {
      wrap[k] = (...args) => { if (k !== 'loadScenario') setCurrent(null); return actions[k](...args); };
    });
    return wrap;
  }, [actions]);

  return (
    <div className="wb-shell">
      <ET s={s} actions={actions} />
      <Hdr s={s} />
      <DemoBar actions={actions} current={current} setCurrent={setCurrent} />
      <div className="wb-grid">
        <div className="wb-stack">
          <MT s={s} actions={liveActions} />
          <SS s={s} actions={liveActions} />
          <RC s={s} actions={liveActions} />
        </div>
        <div className="wb-stack">
          <TP s={s} />
        </div>
        <div className="wb-stack">
          <MP s={s} actions={actions} />
          <CP s={s} />
        </div>
      </div>
      <div className="wb-band"><CS s={s} /></div>
      <div className="wb-band"><EP actions={liveActions} werResult={werResult} werRunning={werRunning} /></div>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')).render(<App />);
