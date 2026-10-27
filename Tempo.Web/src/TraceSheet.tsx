import { useEffect, useState } from 'react'
import { api, type Trace } from './api'

/** Bottom sheet: the agent's run behind a nudge in plain language, provider in the footer
 * (task 12.7, agent-loop spec "Why?"). */
export function TraceSheet({ nudgeId, onClose }: { nudgeId: string; onClose: () => void }) {
  const [trace, setTrace] = useState<Trace | null>(null)
  const [missing, setMissing] = useState(false)

  useEffect(() => {
    api
      .trace(nudgeId)
      .then(setTrace)
      .catch(() => setMissing(true))
  }, [nudgeId])

  return (
    <div className="fixed inset-0 z-50 flex flex-col justify-end" role="dialog" aria-label="Why this nudge">
      <button aria-label="Close" className="absolute inset-0 bg-black/40 border-0" onClick={onClose} />
      <div
        className="relative bg-white rounded-t-3xl px-6 pt-5 flex flex-col gap-4"
        style={{ paddingBottom: 'max(env(safe-area-inset-bottom), 24px)' }}
      >
        <div className="mx-auto w-10 h-1 rounded-full bg-[#E5E7EB]" />
        <div className="text-[13px] font-semibold text-[#4B5563] tracking-[0.06em]">WHY THIS NUDGE</div>
        {missing && <p className="text-[15px] text-[#4B5563]">No trace available for this message.</p>}
        {trace && (
          <>
            <ol className="flex flex-col gap-3">
              {trace.steps.map((step, i) => (
                <li key={i} className="flex gap-3 items-start">
                  <span className="w-6 h-6 rounded-full bg-[#F3F4F6] text-[13px] font-semibold flex items-center justify-center flex-shrink-0 mt-0.5">
                    {i + 1}
                  </span>
                  <span className="text-[15px] leading-snug text-[#111318]">{step}</span>
                </li>
              ))}
            </ol>
            <div className="text-[13px] text-[#6B7280] border-t border-[#E5E7EB] pt-3">
              {trace.provider ? `Decided by Muse Spark (${trace.provider})` : 'Deterministic fallback'} · trigger{' '}
              {trace.trigger} · {trace.outcome}
            </div>
          </>
        )}
      </div>
    </div>
  )
}
