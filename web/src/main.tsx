import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { setAudioSink } from './cascade/cascadeStreamClient'
import { playbackController } from './audio/playbackController'
// H.1 design baseline: tokens FIRST (defines the CSS custom properties + loads the fonts),
// then the workbench class styles that consume them. CSS/markup only — no logic change (ARCH-007).
import './styles/tokens.css'
import './styles/workbench.css'

// Composition-root wiring: route the cascade client's audio frames to the playback controller (D.5
// closes the D.4b onAudio no-op). Raw audio reaches playback only — never the store (invariant #3).
setAudioSink((chunk) => playbackController.enqueue(chunk))

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
