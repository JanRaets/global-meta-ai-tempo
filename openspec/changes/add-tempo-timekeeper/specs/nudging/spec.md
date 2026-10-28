# nudging Specification

## ADDED Requirements

### Requirement: Leave-now nudge
The system SHALL send one push notification per plan version at `leaveAt − nudgeLeadMin` stating the leave time, the mode, the destination and any included stops.

#### Scenario: Nudge sent on time
- **WHEN** the current time reaches `leaveAt − 10 min` for plan version 3 and no nudge exists for `(planId, 3)`
- **THEN** a `Nudge` of kind `Leave` is created and sent, and its text names the mode and leave time in at most two sentences

#### Scenario: Duplicate suppressed
- **WHEN** a nudge already exists for `(planId, 3)`
- **THEN** no second message is sent

#### Scenario: Plan version changes after nudge
- **WHEN** the plan moves to version 4 with `leaveAt` more than 5 minutes different
- **THEN** a new nudge is sent that states it replaces the previous one

### Requirement: Gentle leave-now nudge at the target
*(Added 28 Oct 2026: the buffer is a margin, not a deadline — design D6.)* At `leaveAt` itself, while the plan is in the `LeaveNow` phase (between `leaveAt` and `leaveBy`), the system SHALL send one gentle `LeaveNow` nudge per plan version ("Time to go — leave now … and you're there at HH:mm"): calm, naming the arrival time, no recovery talk. `notify.send` SHALL refuse a `LeaveNow` nudge outside the `LeaveNow` phase.

#### Scenario: Time to go
- **WHEN** the current time passes `leaveAt` but not `leaveBy` and no `LeaveNow` nudge exists for the plan version
- **THEN** one gentle nudge is sent naming the arrival time; the screen stays in normal colours

#### Scenario: Not late yet
- **WHEN** the current time is between `leaveAt` and `leaveBy`
- **THEN** no `Late` nudge fires and the plan is not presented as late

### Requirement: Nudge text validation
`notify.send` SHALL validate the agent's text against the committed plan: a `Leave` nudge must contain the plan's leave time, a `Late` or `LeaveNow` nudge must contain the recovery's/plan's arrival time (the leave time is already in the past and is not required); every nudge must name the mode, be at most two sentences, and contain no time value that does not appear in the committed plan or its options (leaveAt, leaveBy and arriveAt all count). `notify.send` SHALL also refuse a `Leave` nudge before the nudge window opens (`now < leaveAt − nudgeLeadMin`), so no caller can nudge early. *(Amended 27 Oct 2026: Late-nudge time requirement switched from leave time to arrival time; window check moved into the tool per D1 "guards in code". Amended 28 Oct 2026: LeaveNow added.)*

#### Scenario: Wording missing required facts
- **WHEN** the text lacks the leave time
- **THEN** the tool returns `Refused: missing leave time` so the agent can retry; after the run ends without a valid send, the system sends a deterministic template

#### Scenario: Invented time
- **WHEN** the text contains "09:15" and no plan or option has that time
- **THEN** the tool refuses with `Refused: unknown time 09:15`

### Requirement: Late recovery nudge
The system SHALL send one `Late` nudge only on entering the `Late` phase — when the current time passes `leaveBy` (the last moment that still arrives at start), the user is not moving or at the destination, and no `Late` nudge exists for the plan version; the message SHALL propose exactly one recovery option, which the agent chose and committed from `plan.options`. The late minutes count from `leaveBy`, i.e. the minutes the arrival actually slips past the start. *(Amended 28 Oct 2026: previously the Late machinery started at `leaveAt`, treating the buffer as a deadline.)*

### Requirement: Why link
Every nudge SHALL carry a reference to the `AgentTrace` of the run that produced it. The push notification deep-links to the nudge in the app, where "Why?" opens the trace rendered as plain-language steps (no inline button in the notification).

#### Scenario: Why tapped
- **WHEN** the user taps Why? on a late nudge
- **THEN** a message lists the steps (e.g. "Checked 4 options · Only skipping coffee arrives on time · Sent this nudge")

#### Scenario: Skip a stop
- **WHEN** skipping the coffee stop makes the appointment reachable on time and switching mode does not
- **THEN** the nudge proposes skipping the coffee

#### Scenario: Nothing feasible
- **WHEN** no alternative arrives on time
- **THEN** the nudge states the earliest possible arrival and the mode that achieves it

### Requirement: Quiet hours
The system SHALL suppress all nudges during the user's quiet hours except a `Late` nudge for an appointment starting within 60 minutes.

#### Scenario: Habit proposal in quiet hours
- **WHEN** a habit proposal is due at 23:30 and quiet hours are 22:00–07:00
- **THEN** it is deferred to the next batch after 07:00

### Requirement: Web Push transport
The system SHALL deliver nudges and habit proposals as Web Push notifications to every stored `PushSubscription`. A push endpoint answering 410 or 404 means the subscription is expired and SHALL be deleted. *(Replaced the Telegram transport, 27 Oct 2026; design D16.)*

#### Scenario: Send failure
- **WHEN** a push service returns a transient error
- **THEN** the send is retried up to 3 times with backoff and the `Nudge` records the final status

#### Scenario: Expired subscription
- **WHEN** a push endpoint returns 410 Gone
- **THEN** that subscription row is deleted and the send is not retried against it
