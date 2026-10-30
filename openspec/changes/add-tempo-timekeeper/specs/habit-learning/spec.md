# habit-learning Specification

## ADDED Requirements

### Requirement: Habit candidate inference
The system SHALL run a nightly job (and on demand) that groups `Visit` records by `(placeId, contextKey)` where `contextKey = (weekday, timeBucket, direction, adjacentAppointmentKind)`, and SHALL create a `Proposed` habit when a group has at least 3 visits and a duration standard deviation of at most 8 minutes.

#### Scenario: Recurring stop detected
- **WHEN** three Thursday-evening visits to the same supermarket exist on the way toward home with durations 14, 16 and 15 min
- **THEN** one `Habit` in state `Proposed` is created with `avgDurationMin = 15`

#### Scenario: Insufficient evidence
- **WHEN** only two matching visits exist
- **THEN** no habit is created

#### Scenario: Irregular durations
- **WHEN** three visits exist with durations 5, 40 and 90 min
- **THEN** no habit is created

#### Scenario: Already decided
- **WHEN** a `Confirmed` or `Rejected` habit exists for the same `(placeId, contextKey)`
- **THEN** no duplicate is created; a rejected habit is not re-proposed within 30 days

### Requirement: ReturnsHome and StaysPut patterns *(added 30 Oct 2026 — chained-origin amendment)*
The inference job SHALL also look between two placed appointments on the same local day: a home `Visit` inside the gap counts toward a `ReturnsHome` observation; a `Visit` at the previous appointment's place lasting until the next departure (within 15 minutes of the next start) counts toward `StaysPut`. Observations are comparable when the previous appointment's kind, the weekday and the gap bucket (`short` < 1 h, `medium` 1–3 h, `long` ≥ 3 h) match; at least 3 comparable days become a `Proposed` habit with that `Kind`, decided through the same confirmation handler and the same type-level guard as every other habit. A confirmed `ReturnsHome` or `StaysPut` habit overrides the departure-planning gap heuristic for matching look-ahead plans; neither kind ever enters a plan's stop subsets.

#### Scenario: Returning home after hockey is noticed
- **WHEN** on three comparable Fridays a home visit sits between hockey and the evening appointment
- **THEN** one `ReturnsHome` habit is proposed — "After Hockey JO9 you usually head home before the next thing — plan from home?"

#### Scenario: Two days are not a pattern
- **WHEN** only two comparable days exist
- **THEN** no habit is proposed

#### Scenario: Confirmed override
- **WHEN** the user confirms the `ReturnsHome` proposal
- **THEN** matching look-ahead plans depart from home regardless of the gap heuristic

### Requirement: Proposal wording by LLM
The system SHALL ask `IMetaModels.ChatJsonAsync` (Muse Spark) to produce a proposal sentence and a one-line rationale from the candidate's structured facts, and SHALL NOT let the model alter the numeric facts.

#### Scenario: Wording produced
- **WHEN** a candidate exists for a café, weekday mornings, before meetings, 9 min average
- **THEN** the proposal text mentions the place name, the context and the duration, and the stored `avgDurationMin` remains the computed value

### Requirement: User confirmation
The system SHALL deliver proposed habits as one push notification per day deep-linking to `/habits`; on Android the notification carries Confirm/Reject actions, on iOS the user decides in the app. Confirm and reject always route through `ConfirmHabitHandler`. *(Was "over Telegram"; replaced 27 Oct 2026, design D16.)*

#### Scenario: Confirm
- **WHEN** the user taps Confirm
- **THEN** the habit state becomes `Confirmed`, a `ConfirmedHabit` is produced by the confirmation handler, and the planner is re-run

#### Scenario: Reject
- **WHEN** the user taps Reject
- **THEN** the habit state becomes `Rejected` with `decidedAt` set and the planner is unaffected

#### Scenario: No response
- **WHEN** the user does not respond for 7 days
- **THEN** the habit remains `Proposed` and is re-sent once in the next batch

### Requirement: Habit context matching
A `ConfirmedHabit` SHALL match a planning context only when weekday, time bucket and direction all match and, if the habit has an `adjacentAppointmentKind`, that matches as well. *(Amended 29 Oct 2026)* A habit whose context has no specific weekday (`Weekday = null`) SHALL match any Monday–Friday planning context and never a weekend one. Inference always derives concrete weekdays; the generalised form exists only for seeded or hand-edited habits (the demo coffee stop).

#### Scenario: Weekend mismatch
- **WHEN** a weekday-morning coffee habit is evaluated on a Saturday
- **THEN** it does not match

#### Scenario: Weekday-generalised habit
- **WHEN** a habit with no specific weekday (morning, away from home, before a meeting) is evaluated against a Wednesday 10:00 meeting departure from home
- **THEN** it matches — and on a Sunday morning it does not

### Requirement: Habit management API
The system SHALL expose endpoints to list habits by state and to confirm or reject a habit; the app's Habits screen and the Android notification actions use the same endpoints.

#### Scenario: App confirm
- **WHEN** `POST /habits/{id}/confirm` is called
- **THEN** the same confirmation handler runs as for every other confirmation path
