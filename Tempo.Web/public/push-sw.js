// Web Push handlers (design D16), pulled into the generated service worker via
// workbox importScripts. Payload: {kind, title, body, url, nudgeId, habitId, tag}.

self.addEventListener('push', (event) => {
  let data = {}
  try {
    data = event.data ? event.data.json() : {}
  } catch {
    data = { body: event.data ? event.data.text() : '' }
  }
  const isProposal = data.kind === 'HabitProposal'
  event.waitUntil(
    self.registration.showNotification(data.title || 'TEMPO', {
      body: data.body || '',
      icon: '/brand/icon-192.png',
      badge: '/brand/badge-96.png', // monochrome mark: Android status bar renders it white

      data,
      // Same tag per plan: an updated plan replaces the stale notification.
      tag: data.tag || data.nudgeId,
      // Android shows action buttons; iOS ignores them (D12). Only when the payload
      // names a single habit do Confirm/Reject know what to act on.
      actions:
        isProposal && data.habitId
          ? [
              { action: 'confirm', title: 'Confirm' },
              { action: 'reject', title: 'Reject' },
            ]
          : [],
    }),
  )
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const data = event.notification.data || {}

  // Confirm/Reject call the same endpoints as the app's buttons (ConfirmHabitHandler).
  if ((event.action === 'confirm' || event.action === 'reject') && data.habitId) {
    event.waitUntil(fetch(`/api/habits/${data.habitId}/${event.action}`, { method: 'POST' }))
    return
  }

  // Deep link: nudges → /now, habit proposals → /habits (D12/D16).
  const url = data.url || '/'
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
      const open = windows.find((w) => 'focus' in w)
      if (open) {
        open.navigate(url)
        return open.focus()
      }
      return self.clients.openWindow(url)
    }),
  )
})

// iOS (and others) may rotate the push subscription without any page open (added
// 30 Oct 2026, from real use: pushes went dark while the push service kept answering
// 200 on the stale endpoint). Re-subscribe here and re-register, so delivery survives
// a rotation even when the app itself is never reopened.
self.addEventListener('pushsubscriptionchange', (event) => {
  event.waitUntil(
    (async () => {
      let key = event.oldSubscription && event.oldSubscription.options.applicationServerKey
      if (!key) {
        const { publicKey } = await fetch('/push/vapid-public-key').then((r) => r.json())
        const padding = '='.repeat((4 - (publicKey.length % 4)) % 4)
        const base64 = (publicKey + padding).replace(/-/g, '+').replace(/_/g, '/')
        const raw = atob(base64)
        key = new Uint8Array(raw.length)
        for (let i = 0; i < raw.length; i++) key[i] = raw.charCodeAt(i)
      }
      const subscription = await self.registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: key,
      })
      const json = subscription.toJSON()
      await fetch('/push/subscribe', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ endpoint: json.endpoint, keys: json.keys }),
      })
      if (event.oldSubscription && event.oldSubscription.endpoint !== subscription.endpoint) {
        await fetch('/push/subscribe', {
          method: 'DELETE',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ endpoint: event.oldSubscription.endpoint }),
        })
      }
    })(),
  )
})
