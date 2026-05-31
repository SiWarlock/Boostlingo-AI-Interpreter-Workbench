/* ============================================================================
   Demo scenarios — jump the store to canonical states for showcasing.
   window.wbScenario(name, prev) -> new state object.
   ============================================================================ */
(function () {
  const { REALTIME_MODELS, TRANSLATION_MODELS, PHRASES, computeSummary } = window.WB;
  const base = (prev) => ({
    sessionId: prev.sessionId, label: prev.label, mode: prev.mode, direction: prev.direction,
    realtimeModel: prev.realtimeModel, translationModel: prev.translationModel,
    providerHealth: { realtime: 'ready', stt: 'ready', translation: 'ready', tts: 'ready' },
    turns: [], summary: null, errors: [], currentTurn: null,
    sessionStatus: 'configured', turnStatus: 'ready',
  });

  function mkTurn(mode, modelId, phrase, opts = {}) {
    const finalSrc = [{ text: phrase.en, isFinal: true }];
    const finalTgt = [{ text: phrase.es, isFinal: true }];
    if (mode === 'cascade') {
      const tm = TRANSLATION_MODELS.find(m => m.id === modelId);
      const stt = opts.stt ?? 510, tr = opts.tr ?? 680, tts = opts.tts ?? 530;
      const total = stt + tr + tts;
      return { turnId: 't_' + Math.random().toString(36).slice(2,7), mode, direction: { source:'en',target:'es' },
        status: 'completed', sourceTranscript: finalSrc, targetTranscript: finalTgt,
        latency: { stages: { stt, translation: tr, tts }, totalTurnMs: total, speechEndToFirstAudioMs: stt+tr, speechEndToPlaybackMs: total },
        estimatedCostPerMinuteUsd: tm.costMin, estimatedCostUsd: +(tm.costMin*3.8/60).toFixed(4),
        translationModelUsed: tm.label, audioDurationMs: 3800,
        cost: { perMinuteUsd: tm.costMin, model: tm.label, assumption: 'sum of per-stage token + audio pricing' } };
    } else {
      const rm = REALTIME_MODELS.find(m => m.id === modelId);
      const fa = opts.fa ?? rm.base;
      return { turnId: 't_' + Math.random().toString(36).slice(2,7), mode, direction: { source:'en',target:'es' },
        status: 'completed', sourceTranscript: finalSrc, targetTranscript: finalTgt,
        latency: { speechEndToFirstAudioMs: fa, totalTurnMs: fa + 680, speechEndToPlaybackMs: fa + 680 },
        estimatedCostPerMinuteUsd: rm.costMin, estimatedCostUsd: +(rm.costMin*3.8/60).toFixed(4),
        audioDurationMs: 3800,
        cost: { perMinuteUsd: rm.costMin, model: rm.label, assumption: 'input-priced audio; output disclosed-unavailable' } };
    }
  }

  window.wbScenario = function (name, prev) {
    const b = base(prev);
    switch (name) {
      case 'idle':
        return { ...b, sessionStatus: 'idle' };

      case 'fresh':
        return { ...b, sessionStatus: 'configured', turnStatus: 'ready' };

      case 'recording':
        return { ...b, mode: 'realtime', sessionStatus: 'active', turnStatus: 'recording',
          currentTurn: { turnId: 't_rec', mode: 'realtime', direction: { source:'en',target:'es' },
            status: 'recording', sourceTranscript: [{ text: 'Where does it hurt', isFinal: false }],
            targetTranscript: [], latency: {} } };

      case 'streaming': {
        const p = PHRASES[0];
        return { ...b, mode: 'cascade', sessionStatus: 'active', turnStatus: 'processing',
          currentTurn: { turnId: 't_str', mode: 'cascade', direction: { source:'en',target:'es' }, status: 'processing',
            sourceTranscript: [{ text: p.en, isFinal: true }],
            targetTranscript: [{ text: '¿Dónde le duele', isFinal: false }],
            latency: { stages: { stt: 506, translation: 690 } } } };
      }

      case 'completed': {
        const t = mkTurn('realtime', 'gpt-realtime-mini', PHRASES[1], { fa: 840 });
        return { ...b, mode: 'realtime', sessionStatus: 'readyForTurn', turnStatus: 'completed',
          currentTurn: t, turns: [t], summary: computeSummary([t]) };
      }

      case 'comparison': {
        const turns = [
          mkTurn('realtime', 'gpt-realtime-mini', PHRASES[0], { fa: 820 }),
          mkTurn('realtime', 'gpt-realtime-mini', PHRASES[1], { fa: 910 }),
          mkTurn('realtime', 'gpt-realtime', PHRASES[2], { fa: 1040 }),
          mkTurn('realtime', 'gpt-realtime', PHRASES[3], { fa: 1180 }),
          mkTurn('cascade', 'gpt-5.4-mini', PHRASES[0], { stt: 500, tr: 660, tts: 520 }),
          mkTurn('cascade', 'gpt-5.4-mini', PHRASES[4], { stt: 540, tr: 700, tts: 560 }),
          mkTurn('cascade', 'gpt-5.4-nano', PHRASES[1], { stt: 560, tr: 1180, tts: 600 }),
          mkTurn('cascade', 'gpt-5.4-nano', PHRASES[3], { stt: 580, tr: 1260, tts: 620 }),
        ];
        const last = turns[turns.length - 1];
        return { ...b, mode: 'cascade', sessionStatus: 'readyForTurn', turnStatus: 'completed',
          currentTurn: last, turns, summary: computeSummary(turns, { wer: 11 }) };
      }

      case 'error': {
        const t = mkTurn('cascade', 'gpt-5.4-mini', PHRASES[0]);
        return { ...b, mode: 'cascade', sessionStatus: 'active', turnStatus: 'failed',
          turns: [t], summary: computeSummary([t]),
          errors: [
            { code: 'mic_denied', safeMessage: 'Allow mic access in your browser, then retry.', stage: 'capture', retryable: true },
            { code: 'tts_failed', safeMessage: 'Retry, or switch to Realtime to continue.', stage: 'tts', retryable: true },
          ] };
      }
      default:
        return { ...b, sessionStatus: 'configured' };
    }
  };
})();
