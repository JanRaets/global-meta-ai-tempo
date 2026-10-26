# agent-loop Specification

## ADDED Requirements

### Requirement: Tool surface
The system SHALL expose to the agent exactly these deterministic tools, each with a JSON schema and unit tests: `calendar.upcoming(hours)`, `location.current()`, `plan.options(appointmentId)`, `plan.commit(optionId, reason)`, `plan.current(appointmentId)`, `habits.confirmed(appointmentId)`, `notify.send(kind, text)`, `trace.note(text)`.

#### Scenario: Unknown tool
- **WHEN** the model calls a tool name not in the surface
- **THEN** the loop returns an error result to the model and records it in the trace; no action is taken

### Requirement: Agent run
The system SHALL run a Muse Spark tool-calling loop when triggered by a planner tick with changed inputs, `AppointmentChanged`, `VisitCreated`, a habit confirmation, a settings change, a voice intent or a captured appointment. A run ends when the model returns a message without tool calls or after 8 tool calls.

#### Scenario: Normal plan
- **WHEN** an appointment is 40 minutes away and the current plan is stale
- **THEN** the agent calls `plan.options`, commits one option with a reason, and ends; no nudge is sent because the nudge window has not opened

#### Scenario: Nudge window
- **WHEN** the committed plan's `leaveAt − nudgeLeadMin` is now
- **THEN** the agent calls `notify.send("Leave", text)` once and the text names the leave time, mode and stops

#### Scenario: Call limit
- **WHEN** the model makes an 8th tool call
- **THEN** the loop ends after that call and the trace is marked `Truncated`

#### Scenario: Unchanged inputs
- **WHEN** the planner tick fires and no input has changed since the last run
- **THEN** no model call is made

### Requirement: Tools compute, model decides
The agent SHALL NOT be able to set a time, duration or route directly. `plan.commit` SHALL accept only an `optionId` returned by the most recent `plan.options` call in the same run.

#### Scenario: Fabricated option
- **WHEN** the model calls `plan.commit` with an `optionId` not returned in this run
- **THEN** the tool returns an error and nothing is committed

### Requirement: Guards live in tools
`notify.send` SHALL refuse a nudge when one already exists for the same `(planId, version, kind)` or when quiet hours apply (except a `Late` nudge for an appointment within 60 minutes), returning the reason to the model.

#### Scenario: Duplicate nudge attempt
- **WHEN** the model calls `notify.send("Leave", …)` twice in one run
- **THEN** the second call returns `Refused: duplicate` and no message is sent

### Requirement: Late recovery
When invoked in the late state, the agent SHALL commit exactly one recovery option and send exactly one `Late` nudge whose text states the single action and the resulting arrival time taken from the option.

#### Scenario: Skip a stop
- **WHEN** `plan.options` shows that skipping the coffee stop is the only feasible option
- **THEN** the agent commits that option and the nudge proposes skipping coffee

#### Scenario: Nothing feasible
- **WHEN** no option is feasible
- **THEN** the agent commits the least-late option and the nudge states the earliest arrival

### Requirement: Agent trace
Every run SHALL be persisted as an `AgentTrace` with trigger, provider, each tool call (name, arguments, result summary), the final message, duration and outcome.

#### Scenario: Trace available to the app
- **WHEN** `GET /traces/{nudgeId}` is called
- **THEN** the trace for the run that produced the nudge is returned, rendered as a list of plain-language steps

### Requirement: Scripted model for tests
The system SHALL provide a fake `IMetaModels` that replays a scripted sequence of tool calls, so agent behaviour is tested without network.

#### Scenario: Scripted run
- **WHEN** the fake is scripted to call `plan.options` then `plan.commit` then stop
- **THEN** the loop executes those calls against real tools and the resulting plan matches the committed option
