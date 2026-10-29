# departure-planning Specification

## ADDED Requirements

### Requirement: Plan options tool
`plan.options(appointmentId)` SHALL return, using only deterministic code, a list of `PlanOption { optionId, mode, stops, leaveAt, arriveAt, isFeasible, slackMin }` covering every enabled mode and every stop subset (all matching confirmed habits, each one skipped, none), honouring per-plan input changes from voice intents (skipped stop, extra stop, late offset).

#### Scenario: Options enumerated
- **WHEN** two modes are enabled and one habit matches
- **THEN** four options are returned (2 modes × {with stop, without stop}) with distinct `optionId`s

### Requirement: Plan commit tool
`plan.commit(optionId, reason)` SHALL persist the option as the current `DeparturePlan` version with the agent's reason, and SHALL reject an `optionId` not produced by the latest `plan.options` call in the same agent run.

#### Scenario: Commit
- **WHEN** a valid optionId is committed
- **THEN** a new plan version exists with that option's values and the reason text

### Requirement: Deterministic departure plan
The system SHALL compute every `PlanOption` using only deterministic code; no model call SHALL contribute a time value.

#### Scenario: Plan without habits
- **WHEN** the next appointment starts at 10:00, routing estimates 18 min by bicycle from the current location, and the appointment buffer is 5 min
- **THEN** the plan has `leaveAt = 09:37`, `mode = bicycle`, no stops

#### Scenario: Plan with a confirmed habit
- **WHEN** the same inputs apply and a confirmed habit matches the context with `avgDuration = 8 min` and a routing detour of 4 min
- **THEN** the plan has `leaveAt = 09:25` and lists the habit's place as a stop

#### Scenario: Proposed habit ignored
- **WHEN** a habit in `Proposed` state matches the context
- **THEN** the plan is identical to the no-habit plan

### Requirement: Traffic-aware car routing
*(Added 28 Oct 2026.)* Car estimates SHALL come from TomTom Routing with live traffic (`traffic=true`, `departAt`), reading `travelTimeInSeconds` (which includes the delay) and `trafficDelayInSeconds`; bicycle and walking stay on ORS (`Routing:Car:Provider` switches car back to `Ors`). TomTom failures, a missing key or the spent 2,500/day quota (counted, reported on `/admin/model-usage`) fall back to ORS in flight; the straight-line D18 fallback above is unchanged. The delay is a tool-computed fact carried as `trafficDelayMin` on options and the committed plan: from 5 minutes up the plan line reads "… · +N traffic" and the agent context includes it so a nudge may say traffic is bad — the model never computes or states the number.

#### Scenario: Car through TomTom, bike through ORS
- **WHEN** options are computed for car and bicycle
- **THEN** the car estimate (with its `trafficDelayMin`) comes from TomTom and the bicycle estimate from ORS with `trafficDelayMin = 0`

#### Scenario: TomTom down
- **WHEN** TomTom answers 5xx or the daily quota is spent
- **THEN** the car option is computed via ORS without traffic and planning proceeds

#### Scenario: Delay surfaced
- **WHEN** the committed car plan carries `trafficDelayMin = 7`
- **THEN** the Now screen's plan line reads "… min by car · +7 traffic · leave HH:mm"

### Requirement: Mode handling
`plan.options` SHALL include every enabled travel mode unless the user pinned a mode, and SHALL mark each option `isFeasible` by whether it arrives before the appointment buffer.

#### Scenario: Pinned mode
- **WHEN** the user pinned `walking`
- **THEN** only walking options are returned

#### Scenario: Infeasible modes
- **WHEN** no option can reach the appointment on time
- **THEN** every option has `isFeasible = false` and `slackMin` is negative, so the agent can pick the least-late one

### Requirement: Deterministic fallback
If no agent run commits a plan within 2 minutes of a tick (model unavailable), the system SHALL commit the feasible option with the latest `leaveAt` itself, with reason `"fallback: model unavailable"`.

#### Scenario: Model down
- **WHEN** both model providers fail
- **THEN** a plan is still committed deterministically and the nudge uses the template text

### Requirement: Plan versioning
The system SHALL increment the plan version whenever an input (appointment, location, routing estimate beyond ±3 min, confirmed habit set) changes.

#### Scenario: Minor routing jitter
- **WHEN** a re-estimate differs by 2 minutes
- **THEN** the version is unchanged

#### Scenario: Appointment moved
- **WHEN** the appointment start changes
- **THEN** a new plan version is created

### Requirement: Planner tick
The system SHALL run the planner every 2 minutes and on `AppointmentChanged` and `VisitCreated` events, considering appointments within the next 6 hours.

#### Scenario: Nothing upcoming
- **WHEN** there is no appointment with a place in the next 6 hours
- **THEN** no plan is produced and no routing call is made

### Requirement: Type-enforced confirmed habits
`DeparturePlanner.Plan` SHALL accept only `IReadOnlyList<ConfirmedHabit>`, and `ConfirmedHabit` SHALL be constructible only by the habit confirmation handler.

#### Scenario: Constructor visibility
- **WHEN** reflection inspects `ConfirmedHabit`
- **THEN** it has no public constructor

### Requirement: Two leave times — the buffer is a margin
*(Added 28 Oct 2026 from real use: two minutes past leaveAt the screen went red and a Late nudge fired although leaving then still arrived early.)* Every `PlanOption` and committed `DeparturePlan` SHALL carry `leaveAt = start − travel − stops − buffer` (the target: drives nudge timing and the countdown) and `leaveBy = start − travel − stops` (the last moment that still arrives at start). `isFeasible` SHALL mean `arriveAt ≤ start`, and `slackMin` SHALL be the minutes until `leaveBy`. Recovery options are ranked on `arriveAt` against `start`; the buffer plays no part in feasibility.

#### Scenario: Inside the margin is still feasible
- **WHEN** the current time is past `leaveAt` but leaving immediately still arrives by `start`
- **THEN** the option is feasible and the plan is not late

### Requirement: Past appointments show the outcome
*(Added 28 Oct 2026 from real use: a finished school run showed "Leave 08:09 · from here".)* An appointment whose end is in the past — or whose start is more than `Phase:ArrivedFallbackMin` ago (amended 28 Oct, live: a multi-day calendar event got a car plan for a start two days back) — SHALL never be planned again: `plan.options` returns nothing ("plan is frozen"), the tick skips it, and no routing calls are made. For a past appointment with a place, the system SHALL derive once — on arrival or lazily when first viewed — `leftAt` (the EnRoute transition, else the first fix beyond the en-route radius after `leaveAt − 15 min`), `arrivedAt` (the by-proximity Arrived transition, else the Visit at the destination overlapping the appointment; the start+15 fallback is an assumption, not evidence), and an outcome of `OnTime`, `Late(n min past start)` or `NoData`, stored on the plan and never recomputed. Deriving an outcome SHALL NOT create Visits.

#### Scenario: Frozen past plan
- **WHEN** `plan.options` is called for an appointment that has ended
- **THEN** no options and no routing calls are made and the last committed version stays as is

#### Scenario: Outcome line instead of a plan
- **WHEN** the Today screen renders a past placed appointment
- **THEN** the row is greyed and shows "Left 08:11 · arrived 08:18 · on time" (or "… 6 min late", or "No trip recorded") — no mode icon, no origin label — while a future row may still chain from this appointment's place

#### Scenario: Daily summary
- **WHEN** every placed appointment of the day is over and at least one trip has data
- **THEN** Today shows one line — "3 trips · 2 on time · 1 late by 6 min" — and nothing else: no streaks, no scores

### Requirement: Plan phases
Each `DeparturePlan` SHALL carry a phase — `Upcoming`, `Late`, `EnRoute`, `Arrived`, `AtDestination`, `LeaveNow` — evaluated deterministically on every planner tick and on every accepted location post, never by the model. The leave reference (the fix at `leaveAt`, else home) SHALL be snapshotted when `leaveAt` passes. Thresholds come from `Phase:*` configuration. *(Added 27 Oct 2026 from real use; design D6. `AtDestination` and `LeaveNow` added 28 Oct 2026.)*

#### Scenario: Leave now, not late
- **WHEN** the current time is between `leaveAt` and `leaveBy` and the user has not moved
- **THEN** the phase is `LeaveNow`: the Now screen says "Leave now · you'll still arrive on time" in normal colours (an amber accent, never red)

#### Scenario: Late
- **WHEN** the current time passes `leaveBy` and the user is not `EnRoute`
- **THEN** the phase becomes `Late`, the late headline counts the minutes past `leaveBy`, and the current position (or home) remains the leave reference snapshotted at `leaveAt`

#### Scenario: En route
- **WHEN** a location post places the user more than 150 m from the leave reference, whether the phase was `Upcoming` or `Late`
- **THEN** the phase becomes `EnRoute`, recorded as a plain-language trace step

#### Scenario: En route from movement (amended 28 Oct 2026, from real use)
- **WHEN** after `leaveAt − 15 min` two consecutive fixes within `Phase:MovementWindowMinutes` (10) lie more than `Phase:MovementMeters` (300) apart in any direction, or the current fix is more than `Phase:ApproachMeters` (500) closer to the destination than the leave point
- **THEN** the phase becomes `EnRoute` — sparse fix cadence must not leave a drive undetected

#### Scenario: Arrived by proximity
- **WHEN** a location post places the user within 100 m of the destination place
- **THEN** the phase becomes `Arrived` and the plan no longer appears on the Now screen

#### Scenario: Arrived by fallback
- **WHEN** no location fix proves arrival but the appointment started 15 minutes ago
- **THEN** the phase becomes `Arrived`

### Requirement: Already at the destination
*(Added 28 Oct 2026 from real use: two back-to-back meetings at the same office produced a travel plan, a coffee stop and a leave nudge for the second one.)* When the current fix (not stale) is within `Phase:ArrivedMeters` of an upcoming appointment's place before it starts, the plan SHALL be `AtDestination`: a single zero-travel option (no stops, habits not matched, `leaveAt = start`) committed deterministically with reason "already at destination" — no agent run, and `notify.send` refuses every nudge for it. A previous appointment's `Arrived` state SHALL NOT block the next appointment's `AtDestination`. The Now screen shows "You're here" in normal colours.

#### Scenario: Already there
- **WHEN** the user's fresh fix is within the arrive radius of the next appointment's place before it starts
- **THEN** the plan is `AtDestination` with zero travel, no stops and no nudges

#### Scenario: Moved away before the start
- **WHEN** a later fresh fix is beyond the arrive radius before the appointment starts
- **THEN** the plan reverts to `Upcoming` and normal planning resumes (an input change wakes the agent)

### Requirement: Chained origins
*(Added 28 Oct 2026; design D6.)* `plan.options` SHALL take its origin from the current fix only for the NEXT placed appointment; a look-ahead appointment departs from the most recently STARTED placed appointment before it (overlapping events chain from where the user is — amended 28 Oct after a live overlap) when that appointment ends at most `Phase:ChainGapHours` (default 3) before this one starts, else from home. Options and committed plans carry `originKind` (`Current | PreviousAppointment | Home`) and the previous appointment's title. Two appointments at the same `Place`, or within `Phase:ArrivedMeters` of each other, count as the same location and yield the zero-travel option. Habit matching uses the chained origin, not the current fix, for look-ahead plans. The Today screen shows "from here" / "from <previous title>" in the plan line, and "no travel" instead of a leave time for same-place pairs.

#### Scenario: Office, office, dentist
- **WHEN** three placed appointments run office → office → dentist within the chain gap
- **THEN** there is one departure home→office, the middle appointment shows no travel, and one departure office→dentist

#### Scenario: Gap beyond the chain window
- **WHEN** the previous placed appointment ends more than `Phase:ChainGapHours` before the next one starts
- **THEN** the look-ahead origin is home

### Requirement: Late re-estimate in place
While a plan is `Late` or `EnRoute`, the tick SHALL re-run the planner deterministically (no model call) and refresh the committed option's `leaveAt` and `arriveAt` in place for the same mode and stops — same plan version, no new nudge. When a different option would arrive more than 5 minutes earlier than the refreshed committed option, that SHALL count as an input change and the agent runs again. *(Added 27 Oct 2026: the recovery's arrival time froze at nudge time while "N min late" kept ticking.)* *(Amended 29 Oct 2026, from real use: a plan committed at 02:20 still showed its free-flow 16 minutes in the 07:40 rush.)* The same in-place refresh SHALL also run while the plan is `Upcoming` or `LeaveNow` within 60 minutes of `leaveAt` — fresh routing through the 5-minute cache on every tick, so the countdown and travel time track building traffic before departure; the refresh compares `leaveAt` as well as `arriveAt` (before departure the arrival is pinned at start − buffer, so traffic moves `leaveAt` only). Switching options remains the agent's business. *(Amended 29 Oct 2026, from real use: the phase flip rides the ingest while the refresh rode the tick, so "On your way · arrive 08:15" survived minutes into a drive that arrived 08:26.)* The refresh SHALL also run on the ingest path: immediately in the same request when an accepted location post flips a plan to `EnRoute`, and again on every later accepted post while `EnRoute`, throttled to once per `Phase:EnRouteRefreshSeconds` (default 60) or `Phase:EnRouteRefreshMeters` moved (default 300). The tick remains the fallback for sparse posts.

#### Scenario: Arrival time stays honest
- **WHEN** the user is 12 minutes late and the tick re-estimates
- **THEN** the committed option's `arriveAt` moves with reality, the version is unchanged and no nudge is sent

#### Scenario: Morning rush reaches the countdown *(added 29 Oct 2026)*
- **WHEN** an Upcoming plan is within 60 minutes of `leaveAt` and travel grows from 16 to 20 minutes
- **THEN** within a tick the committed plan's `leaveAt` moves 4 minutes earlier in place — same version, no model call, no nudge — and the Now screen's countdown and travel line follow

#### Scenario: Departure gets a live ETA *(added 29 Oct 2026)*
- **WHEN** an accepted location post moves a plan to `EnRoute`
- **THEN** the committed option is re-estimated in place in the same request, so the Now screen's next poll shows a live arrival instead of the pre-departure pinned one

#### Scenario: Posts while driving are throttled *(added 29 Oct 2026)*
- **WHEN** further posts arrive 15 seconds apart without 300 m of movement
- **THEN** at most one refresh runs per 60 seconds; a post 300 m or more from the last refresh goes through early

#### Scenario: A better option appears
- **WHEN** the refreshed committed option arrives more than 5 minutes after an alternative
- **THEN** the agent is invoked to re-decide

### Requirement: No nudges en route
`notify.send` SHALL refuse `Leave` and `Late` nudges when the plan phase is `EnRoute` or `Arrived`, and the tick SHALL NOT invoke the agent for them. *(Added 27 Oct 2026; design D8.)*

#### Scenario: Moving means no late nudge
- **WHEN** the user left late but is moving (phase `EnRoute`) and no `Late` nudge was sent
- **THEN** none is sent at all

### Requirement: Routing fallback
When routing is unavailable for every enabled mode, `plan.options` SHALL return options from a deterministic straight-line estimate (haversine × 1.3 road factor at per-mode speeds), marked `isEstimated`; nudge text for an estimated plan SHALL contain "roughly" (enforced in `notify.send`), and the trace SHALL note "routing unavailable, used estimate". When routing answers again, the next tick SHALL replace the estimate in place, or treat it as an input change when the real `leaveAt` differs by more than 5 minutes. `Routing:FallbackEnabled` disables the fallback. *(Added 27 Oct 2026; design D18.)*

#### Scenario: Total outage
- **WHEN** every enabled mode throws `RoutingUnavailableException`
- **THEN** options are produced from the straight-line estimate with `isEstimated: true`

#### Scenario: Partial outage
- **WHEN** one mode routes and another does not
- **THEN** only real options are returned and nothing is estimated

#### Scenario: Recovery
- **WHEN** a plan is estimated and routing answers again with a leaveAt within 5 minutes
- **THEN** the committed plan's times are replaced in place and the estimate flag clears

#### Scenario: Recovery with a shift
- **WHEN** the real leaveAt differs from the estimate by more than 5 minutes
- **THEN** the agent runs again (input change)
