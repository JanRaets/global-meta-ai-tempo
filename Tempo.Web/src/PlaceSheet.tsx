import { useEffect, useState } from 'react'
import { api, type PlaceCandidate, type PlaceCandidates } from './api'

/** Bottom sheet: "Is this the right place?" (geocoding amendment, 28 Oct 2026). Lists the
 * logged top-3 candidates plus free-text search; picking one re-points the appointment,
 * replans, and stores a sticky correction for the same location text. */
export function PlaceSheet({
  appointmentId,
  onClose,
  onChanged,
}: {
  appointmentId: string
  onClose: () => void
  onChanged?: () => void
}) {
  const [data, setData] = useState<PlaceCandidates | null>(null)
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<PlaceCandidate[] | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.placeCandidates(appointmentId).then(setData).catch(() => setData(null))
  }, [appointmentId])

  const pick = async (candidate: PlaceCandidate) => {
    setBusy(true)
    try {
      const response = await api.setPlace(appointmentId, candidate)
      if (response.ok) {
        onChanged?.()
        onClose()
      }
    } finally {
      setBusy(false)
    }
  }

  const search = async () => {
    if (!query.trim()) return
    setResults(await api.placeSearch(query))
  }

  const Row = ({ candidate }: { candidate: PlaceCandidate }) => (
    <button
      className="flex flex-col items-start gap-0.5 text-left border border-[#E5E7EB] rounded-2xl px-4 py-3 disabled:opacity-40 bg-white"
      disabled={busy}
      onClick={() => void pick(candidate)}
    >
      <span className="text-[15px] font-semibold text-[#111318]">{candidate.name ?? candidate.label}</span>
      <span className="text-[13px] text-[#6B7280]">{candidate.label}</span>
    </button>
  )

  return (
    <div className="fixed inset-0 z-50 flex flex-col justify-end" role="dialog" aria-label="Is this the right place?">
      <button aria-label="Close" className="absolute inset-0 bg-black/40 border-0" onClick={onClose} />
      <div
        className="relative bg-white rounded-t-3xl px-6 pt-5 flex flex-col gap-4 max-h-[80vh] overflow-y-auto"
        style={{ paddingBottom: 'max(env(safe-area-inset-bottom), 24px)' }}
      >
        <div className="mx-auto w-10 h-1 rounded-full bg-[#E5E7EB]" />
        <div className="text-[13px] font-semibold text-[#4B5563] tracking-[0.06em]">IS THIS THE RIGHT PLACE?</div>
        {data?.current && (
          <p className="text-[15px] text-[#4B5563]">
            Now: <span className="font-semibold text-[#111318]">{data.current.name}</span>
          </p>
        )}
        {data && data.candidates.length > 0 && (
          <div className="flex flex-col gap-2">
            {data.candidates.map((candidate, i) => (
              <Row key={i} candidate={candidate} />
            ))}
          </div>
        )}
        <div className="flex gap-2">
          <input
            className="flex-grow rounded-xl border border-[#D1D5DB] px-4 py-3 text-[15px]"
            placeholder="search…"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && void search()}
          />
          <button
            className="rounded-xl bg-[#1F5EFF] px-4 py-3 text-[15px] font-semibold text-white"
            onClick={() => void search()}
          >
            Search
          </button>
        </div>
        {results && (
          <div className="flex flex-col gap-2">
            {results.length === 0 && <p className="text-[14px] text-[#6B7280]">Nothing found.</p>}
            {results.map((candidate, i) => (
              <Row key={i} candidate={candidate} />
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
