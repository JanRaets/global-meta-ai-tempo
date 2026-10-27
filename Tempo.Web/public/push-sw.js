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
      icon: '/icon-192.png',
      badge: '/icon-192.png',
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
