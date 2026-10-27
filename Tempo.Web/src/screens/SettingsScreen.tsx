import { useState } from 'react'
import { api, type Settings } from '../api'
import { enablePush } from '../push'
import { usePoll } from '../usePoll'

const allModes = ['walking', 'bicycle', 'car', 'train'] as const
const modeLabel: Record<string, string> = { walking: 'Walk', bicycle: 'Bike', car: 'Car', train: 'Train' }

/** Settings (task 12.6, design canvas Settings artboard — its Telegram row became
 * push notifications per D16). Every change saves immediately via PUT /api/settings. */
export function SettingsScreen() {
  const { data, refresh, setData } = usePoll<Settings>(api.settings)
  const [pushState, setPushState] = useState<'idle' | 'busy' | 'subscribed' | 'denied' | 'unsupported' | 'tested'>('idle')
  if (!data) return <div className="flex-grow bg-white" />

  const save = (patch: Record<string, unknown>, optimistic: Partial<Settings>) => {
    setData({ ...data, ...optimistic })
    void api.saveSettings(patch).then(refresh, refresh)
  }

  const toggleMode = (mode: string) => {
    const next = data.enabledModes.includes(mode)
      ? data.enabledModes.filter((m) => m !== mode)
      : [...data.enabledModes, mode]
    if (next.length === 0) return // at least one way to travel
    save({ enabledModes: next }, { enabledModes: next })
  }

  const standalone = window.matchMedia('(display-mode: standalone)').matches
  const isIos = /iPad|iPhone|iPod/.test(navigator.userAgent)

  const onEnablePush = async () => {
    setPushState('busy')
    setPushState(await enablePush())
    void refresh()
  }

  const row = 'flex justify-between items-center py-3.5 border-b border-[#E5E7EB] min-h-14'
  const label = 'text-base font-medium'
  const sub = 'text-[13px] text-[#4B5563]'

  return (
    <div className="flex flex-col flex-grow bg-white text-[#111318] overflow-y-auto">
      <div className="px-6" style={{ paddingTop: 'max(env(safe-area-inset-top), 24px)' }}>
        <div className="text-[15px] font-bold tracking-[0.08em]">TEMPO</div>
      </div>
      <div className="px-6 pt-9">
        <div className="text-[34px] font-extrabold tracking-[-0.03em] leading-none">Settings</div>
      </div>

      <div className="px-6 pt-6 pb-6 flex flex-col flex-grow">
        <div className="flex flex-col gap-1.5 py-3.5 border-b border-[#E5E7EB]">
          <label htmlFor="home" className="text-[13px] font-semibold text-[#4B5563]">
            Home
          </label>
          <select
            id="home"
            className="text-base font-medium bg-transparent border-0 p-0 outline-none appearance-none"
            value={data.homePlaceId ?? ''}
            onChange={(e) => save({ homePlaceId: e.target.value }, { homePlaceId: e.target.value })}
          >
            {!data.homePlaceId && <option value="">Choose your home place…</option>}
            {data.places.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </div>

        <div className="flex flex-col gap-2.5 py-3.5 border-b border-[#E5E7EB]">
          <div className="text-[13px] font-semibold text-[#4B5563]">How you travel</div>
          <div className="flex gap-2">
            {allModes.map((mode) => {
              const on = data.enabledModes.includes(mode)
              return (
                <button
                  key={mode}
                  className={`flex-grow min-h-11 rounded-xl text-sm font-semibold ${
                    on ? 'border-0 bg-[#111318] text-white' : 'border-2 border-[#E5E7EB] bg-transparent text-[#4B5563]'
                  }`}
                  onClick={() => toggleMode(mode)}
                >
                  {modeLabel[mode]}
                </button>
              )
            })}
          </div>
        </div>

        <div className="flex flex-col gap-2.5 py-3.5 border-b border-[#E5E7EB]">
          <div className="text-[13px] font-semibold text-[#4B5563]">Pinned mode</div>
          <div className="flex gap-2">
            <button
              className={`flex-grow min-h-11 rounded-xl text-sm font-semibold ${
                !data.pinnedMode ? 'border-0 bg-[#111318] text-white' : 'border-2 border-[#E5E7EB] bg-transparent text-[#4B5563]'
              }`}
              onClick={() => save({ pinnedMode: '' }, { pinnedMode: null })}
            >
              Auto
            </button>
            {data.enabledModes.map((mode) => (
              <button
                key={mode}
                className={`flex-grow min-h-11 rounded-xl text-sm font-semibold ${
                  data.pinnedMode === mode ? 'border-0 bg-[#111318] text-white' : 'border-2 border-[#E5E7EB] bg-transparent text-[#4B5563]'
                }`}
                onClick={() => save({ pinnedMode: mode }, { pinnedMode: mode })}
              >
                {modeLabel[mode]}
              </button>
            ))}
          </div>
        </div>

        <div className={row}>
          <label htmlFor="lead" className={label}>
            Nudge me
          </label>
          <div className="flex items-center gap-2">
            <input
              id="lead"
              type="number"
              min={0}
              max={60}
              className="text-base font-semibold w-12 text-right border-0 border-b-2 border-[#E5E7EB] py-1 bg-transparent outline-none"
              value={data.nudgeLeadMin}
              onChange={(e) => {
                const lead = Math.max(0, Math.min(60, Number(e.target.value) || 0))
                save({ nudgeLeadMin: lead }, { nudgeLeadMin: lead })
              }}
            />
            <span className="text-[15px] text-[#4B5563]">min before leaving</span>
          </div>
        </div>

        <div className={row}>
          <span className={label}>Quiet hours</span>
          <div className="flex items-center gap-1">
            <input
              type="time"
              aria-label="Quiet hours start"
              className="text-base font-semibold bg-transparent border-0 outline-none"
              value={data.quietStart}
              onChange={(e) => save({ quietStart: e.target.value }, { quietStart: e.target.value })}
            />
            <span className="text-[#4B5563]">–</span>
            <input
              type="time"
              aria-label="Quiet hours end"
              className="text-base font-semibold bg-transparent border-0 outline-none"
              value={data.quietEnd}
              onChange={(e) => save({ quietEnd: e.target.value }, { quietEnd: e.target.value })}
            />
          </div>
        </div>

        <div className={row}>
          <div className="flex flex-col gap-0.5 pr-4">
            <span className={label}>Notifications</span>
            <span className={sub}>
              {data.pushSubscriptions > 0
                ? `${data.pushSubscriptions} device(s) subscribed`
                : isIos && !standalone
                  ? 'On iPhone: add TEMPO to your home screen first, then enable'
                  : 'Leave and late nudges as push messages'}
            </span>
          </div>
          {pushState === 'subscribed' || data.pushSubscriptions > 0 ? (
            <button
              className="min-h-10 px-3.5 border-2 border-[#E5E7EB] rounded-xl bg-transparent text-sm font-semibold"
              onClick={() => void api.pushTest().then(() => setPushState('tested'))}
            >
              {pushState === 'tested' ? 'Sent ✓' : 'Send test'}
            </button>
          ) : (
            <button
              className="min-h-10 px-3.5 border-0 rounded-xl bg-[#1F5EFF] text-white text-sm font-semibold disabled:opacity-60"
              disabled={pushState === 'busy'}
              onClick={onEnablePush}
            >
              {pushState === 'busy' ? '…' : pushState === 'denied' ? 'Denied — check browser' : pushState === 'unsupported' ? 'Not supported here' : 'Enable'}
            </button>
          )}
        </div>

        <div className="flex justify-between items-center py-3.5 min-h-14">
          <div className="flex flex-col gap-0.5">
            <span className={label}>Demo mode</span>
            <span className={sub}>Replay a recorded trip instead of live location</span>
          </div>
          <button
            role="switch"
            aria-checked={data.demoMode}
            aria-label={`Demo mode ${data.demoMode ? 'on' : 'off'}`}
            className={`w-13 h-8 border-0 rounded-2xl relative flex-shrink-0 ${data.demoMode ? 'bg-[#1F5EFF]' : 'bg-[#D1D5DB]'}`}
            onClick={() => save({ demoMode: !data.demoMode }, { demoMode: !data.demoMode })}
          >
            <span
              className="absolute top-[3px] w-[26px] h-[26px] rounded-full bg-white transition-all"
              style={{ left: data.demoMode ? 23 : 3 }}
            />
          </button>
        </div>
      </div>
    </div>
  )
}
