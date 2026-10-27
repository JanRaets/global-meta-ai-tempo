import { CalendarIcon, ClockIcon, HabitIcon, SettingsIcon } from './Icons'

export type Tab = 'now' | 'today' | 'habits' | 'settings'

const tabs: { tab: Tab; label: string; Icon: typeof ClockIcon }[] = [
  { tab: 'now', label: 'Now', Icon: ClockIcon },
  { tab: 'today', label: 'Today', Icon: CalendarIcon },
  { tab: 'habits', label: 'Habits', Icon: HabitIcon },
  { tab: 'settings', label: 'Settings', Icon: SettingsIcon },
]

/** Bottom tab bar per the design canvas; `late` recolors it for the red screen. */
export function TabBar({ active, onSelect, late = false }: { active: Tab; onSelect: (tab: Tab) => void; late?: boolean }) {
  return (
    <nav
      aria-label="Main"
      className={`flex justify-around items-center px-2 pt-3 border-t ${
        late ? 'border-white/25' : 'border-[#E5E7EB] bg-white'
      }`}
      style={{ paddingBottom: 'max(env(safe-area-inset-bottom), 18px)' }}
    >
      {tabs.map(({ tab, label, Icon }) => {
        const isActive = tab === active
        const color = late ? (isActive ? 'text-white' : 'text-[#FFD2C2]') : isActive ? 'text-[#111318]' : 'text-[#6B7280]'
        return (
          <button
            key={tab}
            onClick={() => onSelect(tab)}
            className={`flex flex-col items-center gap-1 min-w-16 min-h-11 bg-transparent border-0 text-xs ${
              isActive ? 'font-semibold' : 'font-medium'
            } ${color}`}
          >
            <Icon size={24} strokeWidth={isActive ? 2.2 : 2} knobFill={late ? '#B42318' : '#FFFFFF'} />
            {label}
          </button>
        )
      })}
    </nav>
  )
}
