import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { App } from './App'
import { resyncPush } from './push'

// Re-assert this device's push subscription on every open (30 Oct 2026 — see push.ts).
void resyncPush()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
