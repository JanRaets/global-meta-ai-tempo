import { useEffect, useState } from 'react'

export type LocationStatus = 'off' | 'asking' | 'live' | 'denied' | 'error'

const MIN_INTERVAL_MS = 30_000
const MIN_MOVE_M = 50

function distanceMeters(lat1: number, lng1: number, lat2: number, lng2: number): number {
  const rad = (d: number) => (d * Math.PI) / 180
  const a =
    Math.sin(rad(lat2 - lat1) / 2) ** 2 +
    Math.cos(rad(lat1)) * Math.cos(rad(lat2)) * Math.sin(rad(lng2 - lng1) / 2) ** 2
  return 2 * 6_371_000 * Math.asin(Math.sqrt(a))
}

/** Posts the phone's position to /location while the app is open, at most once per
 *  30 s or on a 50 m move (mobile-app spec: Location from the app). */
export function useLocationPosting(enabled: boolean): LocationStatus {
  const [status, setStatus] = useState<LocationStatus>('off')

  useEffect(() => {
    if (!enabled || !('geolocation' in navigator)) return
    let key: string | null = null
    let lastSentAt = 0
    let lastLat = 0
    let lastLng = 0
    let watchId = 0
    let cancelled = false

    setStatus('asking')
    fetch('/admin/location-key')
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`${r.status}`))))
      .then((body: { key: string }) => {
        if (cancelled) return
        key = body.key
        watchId = navigator.geolocation.watchPosition(
          (pos) => {
            const { latitude, longitude, accuracy } = pos.coords
            const now = Date.now()
            const moved = lastSentAt === 0 || distanceMeters(lastLat, lastLng, latitude, longitude) > MIN_MOVE_M
            if (now - lastSentAt < MIN_INTERVAL_MS && !moved) return
            lastSentAt = now
            lastLat = latitude
            lastLng = longitude
            void fetch(`/location?key=${key}`, {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                lat: latitude,
                lng: longitude,
                timestamp: new Date(pos.timestamp).toISOString(),
                accuracyM: accuracy,
              }),
            })
            setStatus('live')
          },
          (err) => setStatus(err.code === err.PERMISSION_DENIED ? 'denied' : 'error'),
          { enableHighAccuracy: true, maximumAge: 15_000 },
        )
      })
      .catch(() => setStatus('error'))

    return () => {
      cancelled = true
      if (watchId) navigator.geolocation.clearWatch(watchId)
    }
  }, [enabled])

  return status
}
