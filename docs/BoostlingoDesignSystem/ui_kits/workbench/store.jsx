/* ============================================================================
   Workbench mock store — state machine + turn simulation + helpers.
   Pure UI-kit fake: no real audio/network. Mirrors UiSessionState shape.
   Exposes window.useWorkbench(), window.WB (helpers + constants).
   ============================================================================ */
const { useState, useRef, useCallback, useEffect } = React;

/* ---- catalogs ---- */
const REALTIME_MODELS = [
  { id: 'gpt-realtime',      label: 'gpt-realtime',      costMin: 0.24, base: 1080 },
  { id: 'gpt-realtime-mini', label: 'gpt-realtime-mini', costMin: 0.12, base: 840  },
];
const TRANSLATION_MODELS = [
  { id: 'gpt-5.4-mini', label: 'gpt-5.4-mini', costMin: 0.07, base: { stt: 520, tr: 690, tts: 540 } },
  { id: 'gpt-5.4-nano', label: 'gpt-5.4-nano', costMin: 0.045, base: { stt: 540, tr: 980, tts: 560 } },
];

/* ---- scripted EN↔ES phrase pairs ---- */
const PHRASES = [
  { en: 'Where does it hurt the most?',            es: '¿Dónde le duele más?' },
  { en: 'How long have you had these symptoms?',   es: '¿Cuánto tiempo ha tenido estos síntomas?' },
  { en: 'Do you have any allergies to medication?',es: '¿Tiene alguna alergia a algún medicamento?' },
  { en: 'Please sign here to confirm.',            es: 'Por favor, firme aquí para confirmar.' },
  { en: 'Take this twice a day with food.',        es: 'Tome esto dos veces al día con comida.' },
];
const EVAL_PHRASES = [
  'The patient reports mild chest pain.',
  'Schedule a follow-up appointment in two weeks.',
  'Bring your insurance card and a photo ID.',
];

/* ---- latency targets (ms) ---- */
const TARGET = { realtime: 1500, cascade: 3000 };

/* ---- helpers ---- */
const rand = (a, b) => a + Math.random() * (b - a);
const jitter = (v, p = 0.18) => Math.round(v * (1 + rand(-p, p)));

function fmtLatency(ms) {
  if (ms == null || Number.isNaN(ms)) return null;
  if (ms >= 1000) return (ms / 1000).toFixed(2) + ' s';
  return Math.round(ms) + ' ms';
}
function fmtCostMin(v) { return v == null ? null : '$' + v.toFixed(2); }
function fmtCostTurn(v) { return v == null ? null : '$' + v.toFixed(3); }

/* target class for a headline latency given mode */
function latClass(mode, ms) {
  if (ms == null) return 'na';
  const t = TARGET[mode];
  const r = ms / t;
  if (r <= 0.85) return 'good';
  if (r <= 1.0) return 'warn';
  return 'over';
}
function werClass(w) { if (w == null) return 'na'; if (w <= 10) return 'good'; if (w <= 20) return 'warn'; return 'over'; }

const PROVIDERS_DEFAULT = { realtime: 'ready', stt: 'ready', translation: 'ready', tts: 'ready' };

window.WB = { REALTIME_MODELS, TRANSLATION_MODELS, PHRASES, EVAL_PHRASES, TARGET,
  fmtLatency, fmtCostMin, fmtCostTurn, latClass, werClass };

/* ============================================================================ */
function useWorkbench() {
  const [s, setS] = useState(() => ({
    sessionId: 'sess_' + Math.random().toString(36).slice(2, 8),
    label: '',
    mode: 'realtime',
    direction: { source: 'en', target: 'es' },
    realtimeModel: 'gpt-realtime-mini',
    translationModel: 'gpt-5.4-mini',
    sessionStatus: 'configured',   // start at configured so Start is enabled
    turnStatus: 'ready',
    providerHealth: PROVIDERS_DEFAULT,
    currentTurn: null,
    turns: [],
    summary: null,
    errors: [],
  }));
  const timers = useRef([]);
  const patch = useCallback((p) => setS(prev => ({ ...prev, ...(typeof p === 'function' ? p(prev) : p) })), []);
  const clearTimers = () => { timers.current.forEach(clearTimeout); timers.current = []; };
  const after = (ms, fn) => { const t = setTimeout(fn, ms); timers.current.push(t); };
  useEffect(() => clearTimers, []);

  /* ---- config actions ---- */
  const setMode = (m) => patch(prev => prev.turnStatus === 'recording' || prev.turnStatus === 'processing' || prev.turnStatus === 'playing' ? prev : { mode: m });
  const setLabel = (label) => patch({ label, sessionStatus: s.sessionStatus === 'idle' ? 'configured' : s.sessionStatus });
  const setRealtimeModel = (realtimeModel) => patch({ realtimeModel });
  const setTranslationModel = (translationModel) => patch({ translationModel });
  const swapDirection = () => patch(prev => ({ direction: { source: prev.direction.target, target: prev.direction.source } }));

  /* ---- session ---- */
  const startSession = () => {
    if (s.sessionStatus === 'idle' || s.sessionStatus === 'starting' || s.sessionStatus === 'active' || s.sessionStatus === 'readyForTurn') return;
    patch({ sessionStatus: 'starting' });
    after(900, () => patch({ sessionStatus: 'active', turnStatus: 'ready' }));
  };
  const endSession = () => { clearTimers(); patch({ sessionStatus: 'ended', turnStatus: 'ready', currentTurn: null }); };
  const resetSession = () => { clearTimers(); patch({ sessionStatus: 'configured', turnStatus: 'ready', currentTurn: null, turns: [], summary: null, errors: [] }); };

  /* ---- turn ---- */
  const startRecording = () => {
    if (!(s.sessionStatus === 'active' || s.sessionStatus === 'readyForTurn')) return;
    if (!(s.turnStatus === 'ready' || s.turnStatus === 'completed' || s.turnStatus === 'failed')) return;
    const phrase = PHRASES[Math.floor(Math.random() * PHRASES.length)];
    patch({ turnStatus: 'recording', currentTurn: {
      turnId: 'turn_' + Math.random().toString(36).slice(2, 7), mode: s.mode, direction: s.direction,
      status: 'recording', _phrase: phrase, sourceTranscript: [], targetTranscript: [],
      latency: {}, estimatedCostUsd: null, estimatedCostPerMinuteUsd: null,
    }});
  };

  const stopRecording = () => {
    if (s.turnStatus !== 'recording') return;
    patch({ turnStatus: 'processing' });
    runProcessing();
  };

  function streamWords(field, words, baseDelay, perWord, onDone) {
    let acc = '';
    words.forEach((w, i) => {
      after(baseDelay + i * perWord, () => {
        acc = acc ? acc + ' ' + w : w;
        patch(prev => prev.currentTurn ? { currentTurn: { ...prev.currentTurn,
          [field]: [{ text: acc, isFinal: false }] } } : prev);
      });
    });
    after(baseDelay + words.length * perWord + 120, () => {
      patch(prev => prev.currentTurn ? { currentTurn: { ...prev.currentTurn,
        [field]: [{ text: acc, isFinal: true }] } } : prev);
      onDone && onDone();
    });
  }

  function runProcessing() {
    const cur = s.currentTurn; if (!cur) return;
    const mode = cur.mode;
    const srcWords = cur._phrase.en.split(' ');
    const tgtWords = cur._phrase.es.split(' ');

    // stream source
    streamWords('sourceTranscript', srcWords, 250, 90);
    const srcDone = 250 + srcWords.length * 90 + 120;

    if (mode === 'cascade') {
      const tm = TRANSLATION_MODELS.find(m => m.id === s.translationModel);
      const stt = jitter(tm.base.stt), tr = jitter(tm.base.tr), tts = jitter(tm.base.tts);
      // STT fills, then translation, then TTS
      after(srcDone, () => patch(prev => upLat(prev, { stages: { stt } })));
      streamWords('targetTranscript', tgtWords, srcDone + 200, 95, () => {
        after(120, () => patch(prev => upLat(prev, { stages: { stt, translation: tr } })));
      });
      const tgtDone = srcDone + 200 + tgtWords.length * 95 + 120;
      after(tgtDone + 200, () => {
        const total = stt + tr + tts;
        patch(prev => upLat(prev, { stages: { stt, translation: tr, tts },
          totalTurnMs: total, speechEndToFirstAudioMs: stt + tr, speechEndToPlaybackMs: total }));
        patch({ turnStatus: 'playing' });
        after(1400, finalizeTurn);
      });
    } else {
      const rm = REALTIME_MODELS.find(m => m.id === s.realtimeModel);
      const firstAudio = jitter(rm.base);
      streamWords('targetTranscript', tgtWords, srcDone + 120, 70);
      const tgtDone = srcDone + 120 + tgtWords.length * 70 + 120;
      after(srcDone + 300, () => patch(prev => upLat(prev, { speechEndToFirstAudioMs: firstAudio })));
      after(tgtDone + 150, () => {
        patch(prev => upLat(prev, { speechEndToFirstAudioMs: firstAudio,
          totalTurnMs: firstAudio + jitter(700), speechEndToPlaybackMs: firstAudio + jitter(700) }));
        patch({ turnStatus: 'playing' });
        after(1300, finalizeTurn);
      });
    }
  }
  function upLat(prev, lat) {
    if (!prev.currentTurn) return prev;
    return { currentTurn: { ...prev.currentTurn, latency: { ...prev.currentTurn.latency, ...lat,
      stages: { ...(prev.currentTurn.latency.stages||{}), ...(lat.stages||{}) } } } };
  }

  function finalizeTurn() {
    setS(prev => {
      const cur = prev.currentTurn; if (!cur) return prev;
      const mode = cur.mode;
      let costMin, model;
      if (mode === 'realtime') { const rm = REALTIME_MODELS.find(m => m.id === prev.realtimeModel); costMin = jitterCost(rm.costMin); model = rm.label; }
      else { const tm = TRANSLATION_MODELS.find(m => m.id === prev.translationModel); costMin = jitterCost(tm.costMin); model = tm.label; }
      const audioMs = jitter(3800, 0.25);
      const done = { ...cur, status: 'completed', completedAt: Date.now(), audioDurationMs: audioMs,
        estimatedCostPerMinuteUsd: costMin, estimatedCostUsd: +(costMin * audioMs / 60000).toFixed(4),
        translationModelUsed: mode === 'cascade' ? model : undefined,
        cost: { perMinuteUsd: costMin, model, assumption: mode === 'realtime' ? 'input-priced audio; output disclosed-unavailable' : 'sum of per-stage token + audio pricing' } };
      const turns = [...prev.turns, done];
      return { ...prev, turnStatus: 'completed', sessionStatus: 'readyForTurn', currentTurn: done, turns, summary: computeSummary(turns) };
    });
  }
  const jitterCost = (c) => +(c * (1 + rand(-0.1, 0.1))).toFixed(3);

  /* ---- errors (demo) ---- */
  const pushError = (err) => patch(prev => ({ errors: [...prev.errors, { retryable: true, ...err }] }));
  const dismissError = (code) => patch(prev => ({ errors: prev.errors.filter(e => e.code !== code) }));

  /* ---- WER eval ---- */
  const [werResult, setWerResult] = useState(null);
  const [werRunning, setWerRunning] = useState(false);
  const runWerEval = (phraseIdx) => {
    setWerRunning(true); setWerResult(null);
    after(1600, () => {
      const wer = Math.round(rand(4, 18)); const s_ = Math.floor(rand(0, 3)), i = Math.floor(rand(0, 2)), d = Math.floor(rand(0, 2));
      setWerRunning(false);
      setWerResult({ phraseIdx, wer, s: s_, i, d, sampleCount: 1 });
      patch(prev => ({ summary: computeSummary(prev.turns, { wer }) }));
    });
  };

  /* ---- scenario loader (for showcasing states) ---- */
  const loadScenario = useCallback((name) => { clearTimers(); setS(prev => window.wbScenario(name, prev)); setWerResult(null); setWerRunning(false); }, []);

  return { s, actions: { setMode, setLabel, setRealtimeModel, setTranslationModel, swapDirection,
    startSession, endSession, resetSession, startRecording, stopRecording, pushError, dismissError,
    runWerEval, loadScenario }, werResult, werRunning };
}

/* ---- summary computation ---- */
function computeSummary(turns, extra) {
  const byMode = {};
  ['realtime', 'cascade'].forEach(m => {
    const t = turns.filter(x => x.mode === m && x.status === 'completed');
    const lat = t.map(x => m === 'realtime' ? x.latency.speechEndToFirstAudioMs : x.latency.totalTurnMs).filter(Boolean);
    const cost = t.map(x => x.estimatedCostPerMinuteUsd).filter(Boolean);
    byMode[m] = { turnCount: t.length, errorCount: 0,
      avgLatencyMs: lat.length ? Math.round(lat.reduce((a,b)=>a+b,0)/lat.length) : null,
      avgCostMin: cost.length ? +(cost.reduce((a,b)=>a+b,0)/cost.length).toFixed(3) : null };
  });
  const variants = {};
  turns.filter(x=>x.status==='completed').forEach(x => {
    const key = x.mode === 'realtime' ? (x.cost?.model||'realtime') : (x.translationModelUsed||x.cost?.model||'cascade');
    if (!variants[key]) variants[key] = { mode: x.mode, lat: [], cost: [], turns: 0, err: 0 };
    const L = x.mode === 'realtime' ? x.latency.speechEndToFirstAudioMs : x.latency.totalTurnMs;
    if (L) variants[key].lat.push(L); if (x.estimatedCostPerMinuteUsd) variants[key].cost.push(x.estimatedCostPerMinuteUsd);
    variants[key].turns++;
  });
  const variantRows = Object.entries(variants).map(([k,v]) => ({ key: k, mode: v.mode, turns: v.turns, err: v.err,
    avgLatencyMs: v.lat.length ? Math.round(v.lat.reduce((a,b)=>a+b,0)/v.lat.length) : null,
    avgCostMin: v.cost.length ? +(v.cost.reduce((a,b)=>a+b,0)/v.cost.length).toFixed(3) : null }));
  return { byMode, variants: variantRows, wer: extra?.wer != null ? { sampleCount: 1, avgWer: extra.wer } : null };
}

window.useWorkbench = useWorkbench;
window.WB.computeSummary = computeSummary;
