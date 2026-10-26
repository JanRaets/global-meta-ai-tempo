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
