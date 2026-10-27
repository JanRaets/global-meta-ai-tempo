import { useEffect, useState } from 'react'
import { api, type NowPayload } from './api'
import { TabBar, type Tab } from './TabBar'
import { HabitsScreen } from './screens/HabitsScreen'
import { NowScreen } from './screens/NowScreen'
import { SettingsScreen } from './screens/SettingsScreen'
import { TodayScreen } from './screens/TodayScreen'
import { useLocationPosting } from './useLocationPosting'
import { usePoll } from './usePoll'

const pathToTab = (path: string): Tab =>
  path.startsWith('/today') ? 'today' : path.startsWith('/habits') ? 'habits' : path.startsWith('/settings') ? 'settings' : 'now'

/**
 * Tab shell on real paths so push deep links (/now, /habits — design D16) land on the
 * right screen. The late state repaints the whole frame red (D13), tab bar included.
 */
export function App() {
  const [tab, setTab] = useState<Tab>(() => pathToTab(window.location.pathname))
  const locationStatus = useLocationPosting(true)
  // The shell polls /api/now too (cheap, single row reads) so the late repaint covers every tab.
  const { data: now } = usePoll<NowPayload>(api.now)
  const late = now?.state === 'late'

  useEffect(() => {
    const onPop = () => setTab(pathToTab(window.location.pathname))
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  const select = (next: Tab) => {
    setTab(next)
    window.history.pushState(null, '', `/${next}`)
  }

  return (
    <div className={`flex flex-col h-full ${late ? 'bg-[#B42318] text-white' : 'bg-white'}`}>
      {tab === 'now' && <NowScreen locationLive={locationStatus === 'live'} />}
      {tab === 'today' && <TodayScreen />}
      {tab === 'habits' && <HabitsScreen />}
      {tab === 'settings' && <SettingsScreen />}
      <TabBar active={tab} onSelect={select} late={late && tab === 'now'} />
    </div>
  )
}
