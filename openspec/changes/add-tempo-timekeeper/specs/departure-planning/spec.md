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

### Requirement: Plan phases
Each `DeparturePlan` SHALL carry a phase — `Upcoming`, `Late`, `EnRoute`, `Arrived` — evaluated deterministically on every planner tick and on every accepted location post, never by the model. The leave reference (the fix at `leaveAt`, else home) SHALL be snapshotted when `leaveAt` passes. Thresholds come from `Phase:*` configuration. *(Added 27 Oct 2026 from real use; design D6.)*

#### Scenario: Late
- **WHEN** the current time passes `leaveAt` and the user has not moved
- **THEN** the phase becomes `Late` and the current position (or home) is stored as the leave reference

#### Scenario: En route
- **WHEN** a location post places the user more than 150 m from the leave reference, whether the phase was `Upcoming` or `Late`
- **THEN** the phase becomes `EnRoute`, recorded as a plain-language trace step

#### Scenario: Arrived by proximity
- **WHEN** a location post places the user within 100 m of the destination place
- **THEN** the phase becomes `Arrived` and the plan no longer appears on the Now screen

#### Scenario: Arrived by fallback
- **WHEN** no location fix proves arrival but the appointment started 15 minutes ago
- **THEN** the phase becomes `Arrived`

### Requirement: Late re-estimate in place
While a plan is `Late` or `EnRoute`, the tick SHALL re-run the planner deterministically (no model call) and refresh the committed option's `leaveAt` and `arriveAt` in place for the same mode and stops — same plan version, no new nudge. When a different option would arrive more than 5 minutes earlier than the refreshed committed option, that SHALL count as an input change and the agent runs again. *(Added 27 Oct 2026: the recovery's arrival time froze at nudge time while "N min late" kept ticking.)*

#### Scenario: Arrival time stays honest
- **WHEN** the user is 12 minutes late and the tick re-estimates
- **THEN** the committed option's `arriveAt` moves with reality, the version is unchanged and no nudge is sent

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
