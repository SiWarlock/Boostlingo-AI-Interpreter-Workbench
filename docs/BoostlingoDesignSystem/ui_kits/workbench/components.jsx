/* Primitives + Header + Controls (ModeToggle, SessionSetup, RecordingControls) */
const Icon = window.Icon;
const { fmtLatency } = window.WB;

/* ---------- primitives ---------- */
function Card({ children, className = '', style }) {
  return <div className={'card ' + className} style={style}>{children}</div>;
}
function CardHead({ icon, title, right }) {
  return (
    <div className="card-hd">
      {icon && <span className="ic"><Icon name={icon} size={18} /></span>}
      <span className="card-title">{title}</span>
      {right && <span className="right">{right}</span>}
    </div>
  );
}
function Eyebrow({ children, style }) { return <div className="eyebrow" style={style}>{children}</div>; }

const STATUS_KEY = { configured: 'config', readyForTurn: 'ready' };
const STATUS_LABEL = { readyForTurn: 'ready · between turns' };
function statusVar(v){ return STATUS_KEY[v] || v; }

function StatusPill({ value, large }) {
  const k = statusVar(value);
  const label = STATUS_LABEL[value] || value;
  let indicator = <span className="dot" />;
  if (value === 'recording') indicator = <span className="dot dot-pulse" />;
  else if (value === 'starting' || value === 'processing') indicator = <span className="spin" />;
  else if (value === 'playing') indicator = <span className="eqbars"><i/><i/><i/><i/></span>;
  return (
    <span className={'pill ' + (large ? 'pill-lg' : '')}
      style={{ background: `var(--st-${k}-bg)`, color: `var(--st-${k}-fg)` }}>
      {indicator}{label}
    </span>
  );
}

function Button({ variant = 'primary', icon, children, disabled, onClick, block, sm }) {
  return (
    <button className={`btn btn-${variant} ${block ? 'btn-block' : ''} ${sm ? 'btn-sm' : ''} ${disabled ? 'is-disabled' : ''}`}
      disabled={disabled} onClick={onClick}>
      {icon && <span className="ic"><Icon name={icon} size={sm ? 15 : 17} /></span>}
      {children}
    </button>
  );
}

/* ---------- Header ---------- */
function Header({ s }) {
  return (
    <div>
      <div className="header-row">
        <img className="header-mark" src="../../assets/mark.svg" alt="" />
        <div style={{ flex: 1 }}>
          <div className="header-title">AI Interpreter Workbench</div>
          <div className="header-sub">Realtime vs Cascade · live latency, cost &amp; quality · EN ⇄ ES</div>
        </div>
        <StatusPill value={s.sessionStatus} large />
      </div>
      <ProviderChips health={s.providerHealth} />
    </div>
  );
}
function ProviderChips({ health = {} }) {
  const items = [
    { k: 'realtime', label: 'Realtime' }, { k: 'stt', label: 'STT' },
    { k: 'translation', label: 'Translation' }, { k: 'tts', label: 'TTS' },
  ];
  const color = (st) => st === 'ready' ? 'var(--success)' : st === 'error' ? 'var(--danger)' : 'var(--metric-na)';
  return (
    <div className="chips-row">
      <span className="chips-lab">Providers</span>
      {items.map(it => {
        const st = health[it.k] || 'unavailable';
        return (
          <span key={it.k} className={'chip ' + (st !== 'ready' ? 'muted' : '')}>
            <span className="d" style={{ background: color(st) }} />{it.label}
          </span>
        );
      })}
    </div>
  );
}

/* ---------- Mode toggle ---------- */
function ModeToggle({ s, actions }) {
  const locked = ['recording', 'processing', 'playing'].includes(s.turnStatus);
  const rtAvail = s.providerHealth?.realtime === 'ready';
  const csAvail = ['stt', 'translation', 'tts'].every(k => s.providerHealth?.[k] === 'ready');
  const opt = (id, color, icon, title, sub, avail) => (
    <button className={`seg-opt ${color} ${s.mode === id ? 'active' : ''} ${!avail ? 'unavail' : ''}`}
      onClick={() => avail && !locked && actions.setMode(id)}
      title={!avail ? 'Provider not configured' : ''}>
      <span className="top"><span className="ic"><Icon name={icon} size={16} /></span>{title}</span>
      <span className="sub">{avail ? sub : 'not configured'}</span>
    </button>
  );
  return (
    <Card className="card-pad">
      <CardHead icon="sliders" title="Mode"
        right={locked ? <span className="eyebrow" style={{ color: 'var(--fg-3)' }}>locked</span> : null} />
      <div className={'seg ' + (locked ? 'locked' : '')}>
        {opt('realtime', 'blue', 'zap', 'Realtime', 'single live stream', rtAvail)}
        {opt('cascade', 'violet', 'layers', 'Cascade', 'STT → Trans → TTS', csAvail)}
      </div>
    </Card>
  );
}

/* ---------- Session setup ---------- */
function SessionSetup({ s, actions }) {
  const { REALTIME_MODELS, TRANSLATION_MODELS } = window.WB;
  const live = ['active', 'readyForTurn'].includes(s.sessionStatus);
  const starting = s.sessionStatus === 'starting';
  const ended = s.sessionStatus === 'ended';
  const canStart = s.sessionStatus === 'configured';
  return (
    <Card className="card-pad">
      <CardHead icon="headphones" title="Session" />
      <div className="field">
        <span className="field-lab">Label</span>
        <input className="input" placeholder="e.g. clinic intake demo" value={s.label}
          disabled={live || starting} onChange={e => actions.setLabel(e.target.value)} />
      </div>
      <div className="field">
        <span className="field-lab">Direction</span>
        <div className="dir-swap" onClick={actions.swapDirection}>
          {s.direction.source.toUpperCase()}
          <span className="ar"><Icon name="swap" size={16} /></span>
          {s.direction.target.toUpperCase()}
        </div>
      </div>
      <div className="field">
        <span className="field-lab">Realtime model</span>
        <select className="select" value={s.realtimeModel} disabled={live || starting}
          onChange={e => actions.setRealtimeModel(e.target.value)}>
          {REALTIME_MODELS.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
        </select>
      </div>
      <div className="field" style={{ marginBottom: 16 }}>
        <span className="field-lab">Translation model</span>
        <select className="select" value={s.translationModel} disabled={live || starting}
          onChange={e => actions.setTranslationModel(e.target.value)}>
          {TRANSLATION_MODELS.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
        </select>
      </div>
      {!live && !ended &&
        <Button variant="dark" icon="play" block disabled={!canStart || starting} onClick={actions.startSession}>
          {starting ? 'Starting…' : 'Start session'}
        </Button>}
      {live &&
        <Button variant="danger" icon="power" block onClick={actions.endSession}>End session</Button>}
      {ended &&
        <Button variant="ghost" icon="refresh" block onClick={actions.resetSession}>Reset session</Button>}
    </Card>
  );
}

/* ---------- Recording controls ---------- */
function RecordingControls({ s, actions }) {
  const canStart = ['active', 'readyForTurn'].includes(s.sessionStatus) && ['ready', 'completed', 'failed'].includes(s.turnStatus);
  const recording = s.turnStatus === 'recording';
  const busy = ['processing', 'playing'].includes(s.turnStatus);
  return (
    <Card className="card-pad">
      <CardHead icon="mic" title="Recording"
        right={<StatusPill value={recording ? 'recording' : busy ? s.turnStatus : s.turnStatus === 'completed' ? 'completed' : 'ready'} />} />
      {!recording &&
        <Button variant="primary" icon="mic" block disabled={!canStart} onClick={actions.startRecording}>
          {busy ? 'Turn in progress…' : 'Start recording'}
        </Button>}
      {recording &&
        <Button variant="outline" icon="square" block onClick={actions.stopRecording}>Stop</Button>}
      <div style={{ marginTop: 10, fontSize: 12, color: 'var(--fg-3)', display: 'flex', alignItems: 'center', gap: 6 }}>
        <Icon name="headphones" size={14} /> Use a headset to avoid echo.
      </div>
    </Card>
  );
}

Object.assign(window, { Card, CardHead, Eyebrow, StatusPill, Button, Header, ProviderChips, ModeToggle, SessionSetup, RecordingControls });
