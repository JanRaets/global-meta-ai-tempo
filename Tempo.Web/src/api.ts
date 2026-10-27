// Typed client for the PWA's JSON API (task 12.1). All reads repoll every 15 s (12.8);
// the server computes every time value — the client only displays (design D1 habit).

export type NowPayload = {
  state: 'none' | 'normal' | 'late' | 'enroute' | 'arrived'
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

export type RecentNudge = { id: string; kind: string; text: string; time: string; hasTrace: boolean }

export type TimelinePayload = {
  date: string
  items: {
    id: string
    title: string
    start: string
    place: string | null
    isVirtual: boolean
    source: string
    isPast: boolean
    plan: { leaveAt: string; mode: string; stops: string[] } | null
  }[]
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
  confirmHabit: (id: string) => fetch(`/api/habits/${id}/confirm`, { method: 'POST' }),
  rejectHabit: (id: string) => fetch(`/api/habits/${id}/reject`, { method: 'POST' }),
  pauseHabit: (id: string) => fetch(`/api/habits/${id}/pause`, { method: 'POST' }),
  saveSettings: (patch: Record<string, unknown>) =>
    fetch('/api/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(patch) }),
  pushTest: () => fetch('/api/push/test', { method: 'POST' }),
}
