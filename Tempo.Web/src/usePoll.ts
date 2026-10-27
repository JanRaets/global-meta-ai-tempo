import { useCallback, useEffect, useRef, useState } from 'react'

/** Poll a fetcher every 15 s (task 12.8), refresh on tab focus, expose a manual refresh
 * for optimistic flows. Keeps the last good value through transient errors. */
export function usePoll<T>(fetcher: () => Promise<T>, intervalMs = 15_000) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState(false)
  const fetcherRef = useRef(fetcher)
  fetcherRef.current = fetcher

  const refresh = useCallback(async () => {
    try {
      setData(await fetcherRef.current())
      setError(false)
    } catch {
      setError(true) // keep showing the last good data
    }
  }, [])

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
