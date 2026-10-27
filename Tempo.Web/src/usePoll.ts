import { useCallback, useEffect, useRef, useState } from 'react'

/** Poll a fetcher every 15 s (task 12.8), refresh on tab focus, expose a manual refresh
 * for optimistic flows. Keeps the last good value through transient errors; with a
 * persistKey the last good value also survives an offline app start (13.d) — the screen
 * then shows stale data with `error` true until the API answers again. */
export function usePoll<T>(fetcher: () => Promise<T>, intervalMs = 15_000, persistKey?: string) {
  const [data, setData] = useState<T | null>(() => {
    if (!persistKey) return null
    try {
      const raw = localStorage.getItem(persistKey)
      return raw ? (JSON.parse(raw) as T) : null
    } catch {
      return null
    }
  })
  const [error, setError] = useState(false)
  const fetcherRef = useRef(fetcher)
  fetcherRef.current = fetcher

  const refresh = useCallback(async () => {
    try {
      const value = await fetcherRef.current()
      setData(value)
      setError(false)
      if (persistKey) {
        try {
          localStorage.setItem(persistKey, JSON.stringify(value))
        } catch {
          /* per-viewer convenience only */
        }
      }
    } catch {
      setError(true) // keep showing the last good data
    }
  }, [persistKey])

  useEffect(() => {
    void refresh()
    const timer = window.setInterval(refresh, intervalMs)
    const onVisible = () => document.visibilityState === 'visible' && void refresh()
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      window.clearInterval(timer)
      document.removeEventListener('visibilitychange', onVisible)
    }
  }, [refresh, intervalMs])

  return { data, error, refresh, setData }
}
