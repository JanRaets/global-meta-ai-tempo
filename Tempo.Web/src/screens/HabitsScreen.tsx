import { BrandHeader } from '../Brand'
import { api, type Habit } from '../api'
import { ModeIcon, PauseIcon, PlayIcon } from '../Icons'
import { usePoll } from '../usePoll'

/** Habits (task 12.5, design canvas Habits artboard): proposed cards with two big buttons,
 * confirmed list with pause. Confirm/reject/pause update optimistically (12.8). */
export function HabitsScreen() {
  const { data, refresh, setData } = usePoll<Habit[]>(api.habits)
  if (!data) return <div className="flex-grow bg-white" />

  const proposed = data.filter((h) => h.state === 'Proposed')
  const confirmed = data.filter((h) => h.state === 'Confirmed')

  const decide = (habit: Habit, action: 'confirm' | 'reject') => {
    setData(data.map((h) => (h.id === habit.id ? { ...h, state: action === 'confirm' ? 'Confirmed' : 'Rejected' } : h)))
    void (action === 'confirm' ? api.confirmHabit(habit.id) : api.rejectHabit(habit.id)).then(refresh, refresh)
  }

  const pause = (habit: Habit) => {
    setData(data.map((h) => (h.id === habit.id ? { ...h, isPaused: !h.isPaused } : h)))
    void api.pauseHabit(habit.id).then(refresh, refresh)
  }

  return (
    <div className="flex flex-col flex-grow bg-white text-[#111318] overflow-y-auto">
      <div className="px-6" style={{ paddingTop: 'max(env(safe-area-inset-top), 24px)' }}>
        <BrandHeader />
      </div>

      <div className="px-6 pt-9">
        <div className="text-[34px] font-extrabold tracking-[-0.03em] leading-none">Habits</div>
        <div className="text-[15px] text-[#4B5563] mt-2">Things I've noticed. You decide.</div>
      </div>

      <div className="px-6 pt-7 pb-6 flex flex-col gap-4 flex-grow">
        {proposed.map((habit) => (
          <div key={habit.id} className="flex flex-col gap-3.5 p-5 rounded-[20px] bg-[#EEF3FF]">
            <div className="text-[13px] font-semibold text-[#1F5EFF] tracking-[0.06em]">NEW</div>
            <div className="text-[19px] font-semibold leading-[1.3]">
              {habit.sentence ?? `You stop at ${habit.place} regularly — plan for it?`}
            </div>
            {habit.rationale && <div className="text-sm text-[#4B5563]">{habit.rationale}</div>}
            <div className="flex gap-2.5 mt-1">
              <button
                className="flex-grow min-h-13 border-0 rounded-[14px] bg-[#1F5EFF] text-white text-base font-bold"
                onClick={() => decide(habit, 'confirm')}
              >
                Yes, plan for it
              </button>
              <button
                className="flex-grow min-h-13 border-2 border-[#C7D2FE] rounded-[14px] bg-transparent text-[#1F3A8A] text-base font-semibold"
                onClick={() => decide(habit, 'reject')}
              >
                No
              </button>
            </div>
          </div>
        ))}

        {proposed.length === 0 && confirmed.length === 0 && (
          <div className="text-[15px] text-[#4B5563]">
            Watching for patterns — a habit shows up here after three similar stops.
          </div>
        )}

        {confirmed.length > 0 && (
          <div className="text-[13px] font-semibold text-[#4B5563] tracking-[0.06em] mt-3">PLANNING FOR</div>
        )}
        {confirmed.map((habit) => (
          <div key={habit.id} className={`flex items-center gap-3.5 py-3.5 border-b border-[#E5E7EB] ${habit.isPaused ? 'opacity-50' : ''}`}>
            <div className="w-11 h-11 rounded-xl bg-[#F3F4F6] flex items-center justify-center flex-shrink-0">
              <ModeIcon mode="walking" size={22} />
            </div>
            <div className="flex flex-col gap-0.5 flex-grow">
              <div className="text-base font-semibold">{habit.place}</div>
              <div className="text-sm text-[#4B5563]">
                {habit.weekday} {habit.timeBucket.toLowerCase()} · {Math.round(habit.avgDurationMin)} min
                {habit.isPaused && ' · paused'}
              </div>
            </div>
            <button
              aria-label={habit.isPaused ? `Resume ${habit.place}` : `Pause ${habit.place}`}
              className="w-11 h-11 border-0 bg-transparent text-[#6B7280] flex items-center justify-center"
              onClick={() => pause(habit)}
            >
              {habit.isPaused ? <PlayIcon /> : <PauseIcon />}
            </button>
          </div>
        ))}

        <div className="text-[13px] text-[#6B7280] mt-2 leading-[1.4]">
          TEMPO only suggests a habit after seeing the same stop at least three times. Nothing is used until you say yes.
        </div>
      </div>
    </div>
  )
}
