import { useState } from 'react'
import { useLocationPosting } from './useLocationPosting'

export function App() {
  const [sharing, setSharing] = useState(false)
  const status = useLocationPosting(sharing)

  return (
    <main className="flex min-h-dvh flex-col items-center justify-center bg-white px-4 text-center">
      <h1 className="text-4xl font-bold tracking-tight text-[#111318]">TEMPO</h1>
      <p className="mt-2 text-[#4B5563]">
        Nothing to plan yet — the Now screen lands in section 12.
      </p>
      {!sharing && (
        <>
          <p className="mt-8 max-w-xs text-sm text-[#4B5563]">
            TEMPO uses your location to time your departures — nothing is stored beyond 24 hours.
          </p>
          <button
            className="mt-3 rounded-xl bg-[#1F5EFF] px-6 py-3 font-medium text-white"
            onClick={() => setSharing(true)}
          >
            Share location
          </button>
        </>
      )}
      {sharing && (
        <p className="mt-8 flex items-center gap-2 text-sm text-[#4B5563]">
          {status === 'live' && (
            <>
              <span className="h-2.5 w-2.5 rounded-full bg-green-500" /> live
            </>
          )}
          {status === 'asking' && 'waiting for permission…'}
          {status === 'denied' && 'location denied — TEMPO will plan from your home place'}
          {status === 'error' && 'location unavailable'}
        </p>
      )}
    </main>
  )
}
