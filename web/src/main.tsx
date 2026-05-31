import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { setAudioSink, setOnTerminal } from './cascade/cascadeStreamClient'
import { playbackController } from './audio/playbackController'
import { recordingController } from './state/recordingActions'
// H.1 design baseline: tokens FIRST (defines the CSS custom properties + loads the fonts),
// then the workbench class styles that consume them. CSS/markup only — no logic change (ARCH-007).
import './styles/tokens.css'
import './styles/workbench.css'

// Composition-root wiring: route the cascade client's audio frames to the playback controller (D.5
// closes the D.4b onAudio no-op). Raw audio reaches playback only — never the store (invariant #3).
setAudioSink((chunk) => playbackController.enqueue(chunk))

// I.3 auto-VAD: stop the mic when the backend auto-finalizes a cascade turn (no frontend Stop fires).
// The client owns the `done`/terminal dispatch; recordingController owns the capture handle — wire them.
setOnTerminal(() => recordingController.onCascadeTerminal())

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
