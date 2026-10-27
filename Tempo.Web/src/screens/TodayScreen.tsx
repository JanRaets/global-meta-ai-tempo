import { api, type TimelinePayload } from '../api'
import { ModeIcon } from '../Icons'
import { usePoll } from '../usePoll'

/** Today's timeline (task 12.4, design canvas Today artboard). Captured appointments show a
 * small tag; virtual/no-place ones are greyed with no travel line. */
export function TodayScreen() {
  const { data } = usePoll<TimelinePayload>(api.timeline)
  if (!data) return <div className="flex-grow bg-white" />
  const nextIndex = data.items.findIndex((item) => !item.isPast)

  return (
    <div className="flex flex-col flex-grow bg-white text-[#111318] overflow-y-auto">
      <div className="flex justify-between items-center px-6" style={{ paddingTop: 'max(env(safe-area-inset-top), 24px)' }}>
        <div className="text-[15px] font-bold tracking-[0.08em]">TEMPO</div>
        <div className="text-[13px] font-medium text-[#4B5563]">{data.date}</div>
      </div>

      <div className="px-6 pt-9">
        <div className="text-[34px] font-extrabold tracking-[-0.03em] leading-none">Today</div>
        <div className="text-[15px] text-[#4B5563] mt-2">
          {data.items.length === 0
            ? 'Nothing on the calendar.'
            : `${data.items.filter((i) => !i.isVirtual && i.place).length} place(s) to be.`}
        </div>
      </div>

      <div className="px-6 pt-7 pb-6 flex flex-col flex-grow">
        {data.items.map((item, index) => {
          const greyed = item.isVirtual || !item.place
          const isNext = index === nextIndex && !greyed
          const last = index === data.items.length - 1
          return (
            <div key={item.id} className="flex gap-4">
              <div className="flex flex-col items-center w-6 flex-shrink-0">
                <div
                  className={`w-3.5 h-3.5 rounded-full mt-1 box-border ${
                    isNext ? 'bg-[#1F5EFF]' : item.isPast ? 'bg-[#D1D5DB]' : `border-2 ${greyed ? 'border-[#D1D5DB]' : 'border-[#9CA3AF]'}`
                  }`}
                />
                {!last && <div className="w-0.5 flex-grow bg-[#E5E7EB] mt-1.5" />}
              </div>
              <div className={`flex flex-col gap-1.5 flex-grow ${last ? '' : 'pb-7'} ${greyed || item.isPast ? 'text-[#6B7280]' : ''}`}>
                <div className="flex justify-between items-baseline gap-3">
                  <div className="text-[19px] font-bold leading-tight">
                    {item.title}
                    {item.source === 'Captured' && (
                      <span className="ml-2 align-middle text-[11px] font-semibold text-[#1F5EFF] bg-[#EEF3FF] rounded-full px-2 py-0.5">
                        captured
                      </span>
                    )}
                  </div>
                  <div className={`text-[15px] font-semibold flex-shrink-0 ${isNext ? 'text-[#1F5EFF]' : greyed || item.isPast ? '' : 'text-[#4B5563]'}`}>
                    {item.start}
                  </div>
                </div>
                <div className="text-sm text-[#4B5563]">{greyed ? 'Video call · no travel' : item.place}</div>
                {!greyed && item.plan && (
                  <div className={`flex items-center gap-2 mt-1 text-sm font-semibold ${isNext ? '' : 'text-[#4B5563]'}`}>
                    <ModeIcon mode={item.plan.mode} size={18} />
                    Leave {item.plan.leaveAt} · {item.plan.mode}
                    {item.plan.stops.length > 0 && ` · ${item.plan.stops.join(', ')}`}
                  </div>
                )}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
