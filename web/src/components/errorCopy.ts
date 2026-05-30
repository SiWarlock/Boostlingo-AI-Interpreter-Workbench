import type { UiError } from '../types/domain'

// Code -> actionable, sanitized UI copy (ARCH-007 frontend error philosophy / ARCH-018). Reads ONLY the
// UiError.code and returns fixed copy — it NEVER echoes safeMessage, so no raw provider text/stack can
// reach the UI through the banner. (safeMessage is already sanitized backend/D.3-side; errorCopy is the
// second, structural guarantee.) Specific high-value codes first, then stage-family prefixes, then a
// safe generic fallback — never blank, never an error.
export function errorCopy(error: UiError): string {
  switch (error.code) {
    case 'mic.permission_denied':
      return 'Microphone permission denied. Enable mic access and retry.'
    case 'mic.not_found':
      return 'No microphone was found. Connect one and retry.'
    case 'mic.unavailable':
      return 'The microphone is unavailable. Check your device and retry.'
    case 'persistence.failed':
      return 'The session may not have been saved.'
    case 'config.load_failed':
      return 'Could not load server configuration. Refresh to retry.'
    case 'summary.load_failed':
      return 'Could not load the session summary.'
    case 'turn.create_failed':
      return 'Could not start the turn. Retry.'
    case 'rate_limited':
      return 'The provider is rate-limiting requests. Wait a moment and retry.'
    case 'realtime.session.disconnected':
      return 'Realtime connection lost — switch to Cascade mode and retry.'
  }

  const { code } = error
  if (code.startsWith('mic.')) return 'There was a microphone problem. Check your device and retry.'
  if (code.startsWith('stt.'))
    return 'Speech-to-text (STT) failed — check the Deepgram configuration and retry.'
  if (code.startsWith('translation.'))
    return 'Translation failed — check the OpenAI configuration and retry.'
  if (code.startsWith('tts.'))
    return 'Text-to-speech (TTS) failed — check the OpenAI configuration and retry.'
  if (code.startsWith('realtime.'))
    return 'The realtime connection failed. Try switching to Cascade mode.'
  if (code.startsWith('cascade.')) return 'The cascade request failed. Retry.'

  return 'Something went wrong. Please retry.'
}
