// Push subscription helper (design D16). The Settings screen (12.6) calls enablePush()
// from its "Enable notifications" button; iOS requires the home-screen install first.

function base64UrlToUint8Array(base64Url: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64Url.length % 4)) % 4)
  const base64 = (base64Url + padding).replace(/-/g, '+').replace(/_/g, '/')
  const raw = atob(base64)
  const bytes = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i)
  return bytes
}

async function registerCurrentSubscription(registration: ServiceWorkerRegistration): Promise<void> {
  const { publicKey } = await fetch('/push/vapid-public-key').then((r) => r.json())
  const subscription =
    (await registration.pushManager.getSubscription()) ??
    (await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: base64UrlToUint8Array(publicKey),
    }))

  const json = subscription.toJSON()
  await fetch('/push/subscribe', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ endpoint: json.endpoint, keys: json.keys, userAgent: navigator.userAgent }),
  })
}

export async function enablePush(): Promise<'subscribed' | 'denied' | 'unsupported'> {
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) return 'unsupported'
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') return 'denied'

  await registerCurrentSubscription(await navigator.serviceWorker.ready)
  return 'subscribed'
}

/** Silent re-sync on every app open (added 30 Oct 2026, from real use: pushes went dark —
 * the server kept sending to a device-dead endpoint that Apple still answered 200 for.
 * iOS can rotate or drop a web-push subscription without telling anyone, so the stored
 * endpoint is only trustworthy for as long as the app keeps re-asserting it). Acts only
 * when permission is already granted; never prompts, never throws into app start. */
export async function resyncPush(): Promise<void> {
  try {
    if (!('serviceWorker' in navigator) || !('PushManager' in window)) return
    if (Notification.permission !== 'granted') return
    await registerCurrentSubscription(await navigator.serviceWorker.ready)
  } catch {
    // Best effort — the app must start even when the push service misbehaves.
  }
}

export async function disablePush(): Promise<void> {
  const registration = await navigator.serviceWorker.ready
  const subscription = await registration.pushManager.getSubscription()
  if (!subscription) return
  await fetch('/push/subscribe', {
    method: 'DELETE',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ endpoint: subscription.endpoint }),
  })
  await subscription.unsubscribe()
}
