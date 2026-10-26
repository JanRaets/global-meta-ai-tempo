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
A `ConfirmedHabit` SHALL match a planning context only when weekday, time bucket and direction all match and, if the habit has an `adjacentAppointmentKind`, that matches as well.

#### Scenario: Weekend mismatch
- **WHEN** a weekday-morning coffee habit is evaluated on a Saturday
- **THEN** it does not match

### Requirement: Habit management API
The system SHALL expose endpoints to list habits by state and to confirm or reject a habit; the app's Habits screen and the Android notification actions use the same endpoints.

#### Scenario: App confirm
- **WHEN** `POST /habits/{id}/confirm` is called
- **THEN** the same confirmation handler runs as for every other confirmation path
