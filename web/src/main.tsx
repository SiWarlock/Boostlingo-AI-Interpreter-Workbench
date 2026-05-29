import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { setAudioSink } from './cascade/cascadeStreamClient'
import { playbackController } from './audio/playbackController'

// Composition-root wiring: route the cascade client's audio frames to the playback controller (D.5
// closes the D.4b onAudio no-op). Raw audio reaches playback only — never the store (invariant #3).
setAudioSink((chunk) => playbackController.enqueue(chunk))

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
