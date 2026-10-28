// Typed client for the PWA's JSON API (task 12.1). All reads repoll every 15 s (12.8);
// the server computes every time value — the client only displays (design D1 habit).

export type NowPayload = {
  state: 'none' | 'normal' | 'late' | 'enroute' | 'arrived' | 'here' | 'leavenow'
  appointment?: { id: string; title: string; start: string; place: string }
  plan?: {
    version: number
    mode: string
    leaveAt: string
    arriveAt: string
    stops: { name: string; durationMin: number }[]
    isFeasible: boolean
    isEstimated: boolean
    reason: string
    travelMin: number
  } | null
  leaveInMin?: number | null
  lateByMin?: number
  minutesRemaining?: number
  nudges: RecentNudge[]
}

export type RecentNudge = {
  id: string
  kind: string
  text: string
  time: string
  hasTrace: boolean
  appointmentRemoved: boolean
}

export type TripOutcome = {
  kind: 'onTime' | 'late' | 'noData'
  leftAt: string | null
  arrivedAt: string | null
  lateByMin: number | null
}

export type TimelinePayload = {
  date: string
  summary: string | null
  items: {
    id: string
    title: string
    start: string
    place: string | null
    isVirtual: boolean
    source: string
    isPast: boolean
    plan: { leaveAt: string; mode: string; stops: string[]; noTravel: boolean; originLabel: string | null } | null
    outcome: TripOutcome | null
  }[]
}

/** "Left 08:11 · arrived 08:18 · on time" — past rows show what happened, never a plan. */
export function outcomeLabel(outcome: TripOutcome): string {
  if (outcome.kind === 'noData') return 'No trip recorded'
  const parts: string[] = []
  if (outcome.leftAt) parts.push(`Left ${outcome.leftAt}`)
  if (outcome.arrivedAt) parts.push(`arrived ${outcome.arrivedAt}`)
  parts.push(outcome.kind === 'late' ? `${outcome.lateByMin} min late` : 'on time')
  const label = parts.join(' · ')
  return label.charAt(0).toUpperCase() + label.slice(1)
}

export type Habit = {
  id: string
  place: string
  state: 'Proposed' | 'Confirmed' | 'Rejected'
  sentence: string | null
  rationale: string | null
  avgDurationMin: number
  visitCount: number
  weekday: string
  timeBucket: string
  isPaused: boolean
}

export type Settings = {
  homePlaceId: string | null
  homePlaceName: string | null
  enabledModes: string[]
  pinnedMode: string | null
  nudgeLeadMin: number
  quietStart: string
  quietEnd: string
  demoMode: boolean
  timeZone: string
  places: { id: string; name: string }[]
  pushSubscriptions: number
}

export type Trace = { steps: string[]; provider: string | null; trigger: string; outcome: string }

export type PlaceCandidate = { name: string | null; label: string | null; locality: string | null; lat: number; lng: number }
export type PlaceCandidates = {
  rawText: string | null
  current: { id: string; name: string } | null
  candidates: PlaceCandidate[]
}
export type PlaceOverrideRow = { id: string; rawText: string; placeName: string }

export type LocationStats = {
  pointsLastHour: number
  lastPointAt: string | null
  lastPointAgeSeconds: number | null
  isStale: boolean | null
  visitCount: number
}

async function get<T>(url: string): Promise<T> {
  const response = await fetch(url)
  if (!response.ok) throw new Error(`${url}: ${response.status}`)
  return response.json()
}

export const api = {
  now: () => get<NowPayload>('/api/now'),
  timeline: () => get<TimelinePayload>('/api/timeline/today'),
  habits: () => get<Habit[]>('/api/habits'),
  settings: () => get<Settings>('/api/settings'),
  trace: (nudgeId: string) => get<Trace>(`/api/traces/${nudgeId}`),
  locationStats: () => get<LocationStats>('/admin/location-stats'),
  placeCandidates: (appointmentId: string) => get<PlaceCandidates>(`/api/appointments/${appointmentId}/place-candidates`),
  placeSearch: (q: string) => get<PlaceCandidate[]>(`/api/places/search?q=${encodeURIComponent(q)}`),
  setPlace: (appointmentId: string, c: PlaceCandidate) =>
    fetch(`/api/appointments/${appointmentId}/place`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ lat: c.lat, lng: c.lng, name: c.label ?? c.name }),
    }),
  placeOverrides: () => get<PlaceOverrideRow[]>('/api/place-overrides'),
  removePlaceOverride: (id: string) => fetch(`/api/place-overrides/${id}`, { method: 'DELETE' }),
  confirmHabit: (id: string) => fetch(`/api/habits/${id}/confirm`, { method: 'POST' }),
  rejectHabit: (id: string) => fetch(`/api/habits/${id}/reject`, { method: 'POST' }),
  pauseHabit: (id: string) => fetch(`/api/habits/${id}/pause`, { method: 'POST' }),
  saveSettings: (patch: Record<string, unknown>) =>
    fetch('/api/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) }),
  pushTest: () => fetch('/api/push/test', { method: 'POST' }),
}
