import { useState } from 'react'
import { api, type NowPayload } from '../api'
import { BrandHeader } from '../Brand'
import { CapturePhoto } from '../CapturePhoto'
import { HoldToTalk } from '../HoldToTalk'
import { ModeIcon } from '../Icons'
import { TraceSheet } from '../TraceSheet'
import { usePoll } from '../usePoll'

/** The Now screen (task 12.3, design canvas Main + Late artboards): one dominant number,
 * the committed plan as the reason line, voice input, recent nudges with Why?. */
export function NowScreen({ locationLive }: { locationLive: boolean }) {
  const { data, error, refresh } = usePoll<NowPayload>(api.now, 15_000, 'tempo:last-now')
  const [traceFor, setTraceFor] = useState<string | null>(null)

  if (!data) return <Shell locationLive={locationLive} />
  const late = data.state === 'late'

  return (
    <div className={`flex flex-col flex-grow overflow-y-auto ${late ? 'bg-[#B42318] text-white' : 'bg-white text-[#111318]'}`}>
      <Header locationLive={locationLive} late={late} reconnecting={error} />

      {data.state === 'none' && (
        <div className="px-6 pt-11 flex flex-col gap-2">
          <div className="text-[34px] font-extrabold tracking-[-0.03em] leading-none">All clear</div>
          <p className="text-[15px] text-[#4B5563]">
            Nothing to plan in the next six hours — the next appointment with a place starts the clock.
          </p>
        </div>
      )}

      {data.state === 'arrived' && data.appointment && (
        <div className="px-6 pt-11 flex flex-col gap-2">
          <div className="text-[34px] font-extrabold tracking-[-0.03em] leading-none">Made it</div>
          <p className="text-[15px] text-[#4B5563]">
            {data.appointment.title} · {data.appointment.start}
          </p>
        </div>
      )}

      {data.state === 'here' && data.appointment && (
        <div className="px-6 pt-11 flex flex-col gap-2">
          <div className="text-[34px] font-extrabold tracking-[-0.03em] leading-none">You&rsquo;re here</div>
          <p className="text-[15px] text-[#4B5563]">
            {data.appointment.title} · {data.appointment.start}
          </p>
        </div>
      )}

      {data.state === 'leavenow' && data.appointment && (
        <div className="px-6 pt-11 flex flex-col gap-2">
          <div className="text-[44px] font-extrabold tracking-[-0.03em] leading-none text-[#B45309]">Leave now</div>
          <p className="text-[15px] text-[#4B5563]">
            you&rsquo;ll still arrive on time{data.plan ? ` · by ${data.plan.arriveAt}` : ''}
          </p>
          <p className="text-[15px] text-[#111318] font-medium pt-1">
            {data.appointment.title} · {data.appointment.start}
          </p>
          {data.plan && (
            <div className="mt-4 flex items-center gap-3.5 px-4.5 py-4 bg-[#F3F4F6] rounded-2xl">
              <ModeIcon mode={data.plan.mode} size={28} />
              <div className="text-base font-semibold">
                By {data.plan.mode} · arrive {data.plan.arriveAt}
              </div>
            </div>
          )}
        </div>
      )}

      {data.state !== 'none' && data.state !== 'arrived' && data.state !== 'here' && data.state !== 'leavenow' && data.appointment && (
        <>
          <div className="px-6 pt-11 flex flex-col gap-1.5">
            <div className={`text-[15px] font-medium ${late ? 'text-[#FFE4D6]' : 'text-[#4B5563]'}`}>
              {late ? "You're running" : data.state === 'enroute' ? 'On your way' : 'Leave in'}
            </div>
            <div className="flex items-baseline gap-2.5">
              <div className="text-[128px] font-extrabold leading-none tracking-[-0.04em]">
                {late ? data.lateByMin : data.state === 'enroute' ? data.minutesRemaining : Math.max(0, data.leaveInMin ?? 0)}
              </div>
              <div className={`text-[32px] font-semibold ${late ? 'text-[#FFE4D6]' : 'text-[#4B5563]'}`}>
                {late ? 'min late' : data.state === 'enroute' ? 'min to go' : 'min'}
              </div>
            </div>
          </div>

          <div className="px-6 pt-7 flex flex-col gap-2.5">
            <div className="text-[26px] font-bold leading-[1.15] tracking-[-0.02em]">{data.appointment.title}</div>
            <div className={`text-base ${late ? 'text-[#FFE4D6]' : 'text-[#4B5563]'}`}>
              {data.appointment.start} · {data.appointment.place}
            </div>
          </div>

          {late && data.plan && (
            <div className="mx-6 mt-9 flex flex-col gap-3">
              <div className="text-[17px] leading-[1.4] font-medium">{data.plan.reason}</div>
              <div className="flex items-center justify-center gap-2.5 w-full min-h-16 rounded-[18px] bg-white text-[#B42318] text-[19px] font-bold">
                <ModeIcon mode={data.plan.mode} size={22} strokeWidth={2.4} />
                Leave now by {data.plan.mode} — arrive {data.plan.arriveAt}
              </div>
            </div>
          )}

          {!late && data.plan && (
            <div className="mx-6 mt-7 flex items-center gap-3.5 px-4.5 py-4 bg-[#F3F4F6] rounded-2xl">
              <ModeIcon mode={data.plan.mode} size={28} />
              <div className="flex flex-col gap-0.5">
                <div className="text-base font-semibold">
                  {data.state === 'enroute'
                    ? `By ${data.plan.mode} · arrive ${data.plan.arriveAt}`
                    : `${data.plan.travelMin} min by ${data.plan.mode}${(data.plan.trafficDelayMin ?? 0) >= 5 ? ` · +${data.plan.trafficDelayMin} traffic` : ''} · leave ${data.plan.leaveAt}`}
                  {data.plan.isEstimated && (
                    <span className="ml-2 align-middle text-[11px] font-semibold text-[#6B7280] bg-[#E5E7EB] rounded-full px-2 py-0.5">
                      estimated
                    </span>
                  )}
                </div>
                {data.plan.stops.length > 0 && (
                  <div className="text-sm text-[#4B5563]">
                    + {data.plan.stops.map((s) => `${s.durationMin} min at ${s.name}`).join(', ')}, on the way
                  </div>
                )}
              </div>
            </div>
          )}
        </>
      )}

      {!late && (
        <div className="mt-7 flex flex-wrap items-center justify-end gap-3 px-6">
          <HoldToTalk onApplied={refresh} />
          <CapturePhoto onApplied={refresh} />
        </div>
      )}

      {data.nudges.length > 0 && (
        <div className="px-6 pt-8 pb-6 flex flex-col gap-3 flex-grow">
          <div className={`text-[13px] font-semibold tracking-[0.06em] ${late ? 'text-[#FFE4D6]' : 'text-[#4B5563]'}`}>
            RECENT
          </div>
          <div className="flex flex-col gap-2.5">
            {data.nudges.map((nudge) => (
              <button
                key={nudge.id}
                className="flex gap-3 items-start text-left bg-transparent border-0 p-0"
                onClick={() => nudge.hasTrace && setTraceFor(nudge.id)}
              >
                <span className={`text-[13px] w-11 flex-shrink-0 pt-0.5 ${late ? 'text-[#FFD2C2]' : 'text-[#6B7280]'}`}>
                  {nudge.time}
                </span>
                <span className={`text-[15px] leading-[1.35] flex-grow ${nudge.appointmentRemoved ? 'text-[#9CA3AF]' : ''}`}>
                  {nudge.text}
                  {nudge.appointmentRemoved && <span className="ml-1.5 text-[12px] text-[#9CA3AF]">· appointment removed</span>}
                </span>
                {nudge.hasTrace && (
                  <span className={`text-[13px] font-semibold flex-shrink-0 ${late ? 'text-[#FFE4D6]' : 'text-[#1F5EFF]'}`}>
                    Why?
                  </span>
                )}
              </button>
            ))}
          </div>
        </div>
      )}

      {traceFor && <TraceSheet nudgeId={traceFor} onClose={() => setTraceFor(null)} />}
    </div>
  )
}

function Shell({ locationLive }: { locationLive: boolean }) {
  return (
    <div className="flex flex-col flex-grow bg-white text-[#111318]">
      <Header locationLive={locationLive} late={false} />
    </div>
  )
}

function Header({ locationLive, late, reconnecting = false }: { locationLive: boolean; late: boolean; reconnecting?: boolean }) {
  return (
    <div className="flex justify-between items-center px-6" style={{ paddingTop: 'max(env(safe-area-inset-top), 24px)' }}>
      <BrandHeader />
      <div className={`flex items-center gap-1.5 text-[13px] font-medium ${late ? 'text-[#FFE4D6]' : 'text-[#4B5563]'}`}>
        <span
          className={`w-2 h-2 rounded-full inline-block ${
            reconnecting ? 'bg-[#F59E0B]' : late ? 'bg-white' : locationLive ? 'bg-[#1F5EFF]' : 'bg-[#D1D5DB]'
          }`}
        />
        {reconnecting ? 'reconnecting…' : locationLive ? 'live' : 'no location'}
      </div>
    </div>
  )
}
