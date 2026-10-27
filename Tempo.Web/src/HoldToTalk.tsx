import { useEffect, useRef, useState } from 'react'
import { WavRecorder } from './audio/wavRecorder'

type VoiceResult = {
  intentId: string | null
  transcript: string
  kind: string
  applied: boolean
  change: string
  suggestions: string[]
}

type Phase = 'idle' | 'recording' | 'listening' | 'micDenied'

/**
 * Hold-to-talk + result card (voice-input spec). Pointer events cover touch and mouse;
 * the recorder is created inside the press handler (iOS AudioContext rule). When the
 * microphone is denied, a text field with the three intent chips takes its place.
 */
export function HoldToTalk({ onApplied }: { onApplied?: () => void } = {}) {
  const [phase, setPhase] = useState<Phase>('idle')
  const [result, setResult] = useState<VoiceResult | null>(null)
  const [undone, setUndone] = useState(false)
  const [undoVisible, setUndoVisible] = useState(false)
  const [text, setText] = useState('')
  const recorder = useRef<WavRecorder | null>(null)
  const undoTimer = useRef<number | undefined>(undefined)

  useEffect(() => () => window.clearTimeout(undoTimer.current), [])

  const showResult = (r: VoiceResult) => {
    setResult(r)
    setUndone(false)
    setUndoVisible(r.applied && r.intentId !== null)
    window.clearTimeout(undoTimer.current)
    undoTimer.current = window.setTimeout(() => setUndoVisible(false), 10_000) // Undo for 10 s
    if (r.applied) onApplied?.()
  }

  const press = async () => {
    if (phase !== 'idle') return
    try {
      const rec = new WavRecorder()
      await rec.start() // inside the gesture, for iOS
      recorder.current = rec
      setPhase('recording')
    } catch {
      setPhase('micDenied') // spec: text field with the same three intents
    }
  }

  const release = async () => {
    if (phase !== 'recording' || !recorder.current) return
    setPhase('listening')
    try {
      const wav = await recorder.current.stop()
      const response = await fetch('/api/voice', {
        method: 'POST',
        headers: { 'Content-Type': 'audio/wav' },
        body: wav,
      })
      if (!response.ok) throw new Error(await response.text())
      showResult(await response.json())
    } catch {
      setResult(null)
    } finally {
      recorder.current = null
      setPhase('idle')
    }
  }

  const sendText = async (value: string) => {
    if (!value.trim()) return
    setPhase('listening')
    try {
      const response = await fetch('/api/voice/text', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: value }),
      })
      if (response.ok) showResult(await response.json())
    } finally {
      setText('')
      setPhase('micDenied')
    }
  }

  const undo = async () => {
    if (!result?.intentId) return
    const response = await fetch(`/api/voice/undo/${result.intentId}`, { method: 'POST' })
    if (response.ok) {
      setUndone(true)
      setUndoVisible(false)
      onApplied?.()
    }
  }

  const chips = ['Skip a stop', 'Add a stop', 'Running late']

  return (
    <div className="mt-10 flex w-full max-w-sm flex-col items-center gap-4">
      {phase !== 'micDenied' ? (
        <button
          className={`select-none rounded-full px-8 py-5 font-medium text-white transition-colors ${
            phase === 'recording' ? 'bg-red-500' : 'bg-[#1F5EFF]'
          }`}
          style={{ touchAction: 'none' }}
          onPointerDown={press}
          onPointerUp={release}
          onPointerCancel={release}
          onContextMenu={(e) => e.preventDefault()}
        >
          {phase === 'recording' ? 'Listening — release to send' : phase === 'listening' ? '…' : 'Hold to talk'}
        </button>
      ) : (
        <div className="flex w-full flex-col gap-2">
          <input
            className="w-full rounded-xl border border-[#D1D5DB] px-4 py-3"
            placeholder="Microphone denied — type instead"
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && sendText(text)}
          />
          <div className="flex gap-2">
            {chips.map((chip) => (
              <button
                key={chip}
                className="rounded-full border border-[#1F5EFF] px-3 py-1 text-sm text-[#1F5EFF]"
                onClick={() => sendText(chip)}
              >
                {chip}
              </button>
            ))}
          </div>
        </div>
      )}
      {phase === 'listening' && <p className="text-sm text-[#4B5563]">listening…</p>}
      {result && (
        <div className="w-full rounded-2xl border border-[#E5E7EB] bg-[#F9FAFB] p-4 text-left">
          <p className="text-sm italic text-[#4B5563]">“{result.transcript}”</p>
          <p className="mt-2 font-medium text-[#111318]">{undone ? 'Undone.' : result.change}</p>
          {result.kind === 'Unknown' && result.suggestions.length > 0 && (
            <div className="mt-2 flex gap-2">
              {chips.map((chip) => (
                <button
                  key={chip}
                  className="rounded-full border border-[#1F5EFF] px-3 py-1 text-sm text-[#1F5EFF]"
                  onClick={() => sendText(chip)}
                >
                  {chip}
                </button>
              ))}
            </div>
          )}
          {undoVisible && !undone && (
            <button className="mt-3 text-sm font-medium text-[#1F5EFF]" onClick={undo}>
              Undo
            </button>
          )}
        </div>
      )}
    </div>
  )
}
