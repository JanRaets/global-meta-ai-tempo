import { useRef, useState } from 'react'
import { resizeImage } from './capture/imageResize'
import { CameraIcon } from './Icons'

type CaptureResponse = {
  title: string
  startLocal: string | null
  placeText: string
  placeName: string | null
  hasLocation: boolean
  confidence: number
  rawText: string
  found: boolean
}

type Phase = 'idle' | 'uploading' | 'card' | 'added' | 'error'

/**
 * Camera button + confirm card (capture-appointment spec). Mounted on the Now screen
 * below hold-to-talk: photo → client-side resize → POST /api/capture → card (title,
 * editable time, place, thumbnail, Add/Retake). On Add the Now screen refreshes so
 * the new appointment's countdown shows immediately.
 */
export function CapturePhoto({ onApplied }: { onApplied?: () => void } = {}) {
  const [phase, setPhase] = useState<Phase>('idle')
  const [capture, setCapture] = useState<CaptureResponse | null>(null)
  const [startLocal, setStartLocal] = useState('')
  const [thumbnail, setThumbnail] = useState<string | null>(null)
  const [message, setMessage] = useState('')
  const fileInput = useRef<HTMLInputElement>(null)

  const reset = () => {
    if (thumbnail) URL.revokeObjectURL(thumbnail)
    setPhase('idle')
    setCapture(null)
    setStartLocal('')
    setThumbnail(null)
    setMessage('')
    if (fileInput.current) fileInput.current.value = ''
  }

  const onFile = async (file: File) => {
    setPhase('uploading')
    setMessage('')
    setThumbnail(URL.createObjectURL(file))
    try {
      const resized = await resizeImage(file)
      const response = await fetch('/api/capture', {
        method: 'POST',
        headers: { 'Content-Type': 'image/jpeg' },
        body: resized,
      })
      if (!response.ok) throw new Error(await response.text())
      const result = (await response.json()) as CaptureResponse
      setCapture(result)
      setStartLocal(result.startLocal ?? '')
      setPhase('card')
    } catch {
      setPhase('error')
      setMessage('Could not read the photo — try again.')
    }
  }

  const add = async () => {
    if (!capture || !startLocal) return
    setPhase('uploading')
    try {
      const response = await fetch('/api/capture/confirm', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ title: capture.title, startLocal, placeText: capture.placeText }),
      })
      if (!response.ok) throw new Error(await response.text())
      setPhase('added')
      onApplied?.()
    } catch {
      setPhase('card')
      setMessage('Could not add it — check the time and try again.')
    }
  }

  return (
    <div className="mt-4 flex w-full max-w-sm flex-col items-center gap-4">
      <input
        ref={fileInput}
        type="file"
        accept="image/*"
        capture="environment"
        className="hidden"
        onChange={(e) => e.target.files?.[0] && void onFile(e.target.files[0])}
      />
      {phase === 'idle' && (
        <button
          className="flex items-center gap-2 rounded-full border-2 border-[#1F5EFF] px-6 py-3 font-semibold text-[#1F5EFF]"
          onClick={() => fileInput.current?.click()}
        >
          <CameraIcon size={20} />
          Capture invite
        </button>
      )}
      {phase === 'uploading' && <p className="text-sm text-[#4B5563]">reading…</p>}
      {phase === 'error' && (
        <div className="flex flex-col items-center gap-3">
          <p className="text-sm text-[#4B5563]">{message}</p>
          <button
            className="flex items-center gap-2 rounded-full border-2 border-[#1F5EFF] px-6 py-3 font-semibold text-[#1F5EFF]"
            onClick={() => fileInput.current?.click()}
          >
            <CameraIcon size={20} />
            Retake
          </button>
        </div>
      )}
      {phase === 'card' && capture && (
        <div className="w-full rounded-2xl border border-[#E5E7EB] bg-[#F9FAFB] p-4 text-left">
          {thumbnail && (
            <img src={thumbnail} alt="Captured invite" className="mb-3 max-h-40 w-full rounded-xl object-cover" />
          )}
          {capture.found ? (
            <>
              <p className="font-medium text-[#111318]">{capture.title}</p>
              <input
                type="datetime-local"
                aria-label="Appointment time"
                className="mt-2 w-full rounded-xl border border-[#D1D5DB] px-3 py-2"
                value={startLocal}
                onChange={(e) => setStartLocal(e.target.value)}
              />
              <p className="mt-2 text-sm text-[#4B5563]">
                {capture.hasLocation ? (capture.placeName ?? capture.placeText) : 'no location'}
              </p>
              {capture.confidence < 0.5 && (
                <p className="mt-2 rounded-xl bg-amber-100 px-3 py-2 text-sm text-amber-800">
                  Unsure about this one — check the time before adding.
                </p>
              )}
              <div className="mt-3 flex gap-3">
                <button
                  className="flex-1 rounded-xl bg-[#1F5EFF] px-4 py-3 font-medium text-white disabled:opacity-40"
                  disabled={!startLocal}
                  onClick={() => void add()}
                >
                  Add
                </button>
                <button
                  className="flex-1 rounded-xl border border-[#D1D5DB] px-4 py-3 font-medium text-[#111318]"
                  onClick={reset}
                >
                  Retake
                </button>
              </div>
            </>
          ) : (
            <>
              <p className="font-medium text-[#111318]">No appointment found</p>
              <p className="mt-1 text-sm text-[#4B5563]">
                {capture.rawText ? `Read: “${capture.rawText}”` : 'Could not read anything.'}
              </p>
              <button
                className="mt-3 w-full rounded-xl bg-[#1F5EFF] px-4 py-3 font-medium text-white"
                onClick={reset}
              >
                Retake
              </button>
            </>
          )}
          {message && <p className="mt-2 text-sm text-red-600">{message}</p>}
        </div>
      )}
      {phase === 'added' && (
        <div className="flex flex-col items-center gap-3">
          <p className="font-medium text-[#111318]">Added — TEMPO will plan for it.</p>
          <button
            className="rounded-full border border-[#D1D5DB] px-6 py-2 font-medium text-[#111318]"
            onClick={reset}
          >
            Take another
          </button>
        </div>
      )}
    </div>
  )
}
