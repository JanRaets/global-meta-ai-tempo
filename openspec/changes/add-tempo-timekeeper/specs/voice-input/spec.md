# voice-input Specification

## ADDED Requirements

### Requirement: Hold-to-talk
The app SHALL provide a hold-to-talk button on the Now screen that records up to 15 seconds of audio and posts it to `POST /voice` on release.

#### Scenario: Short utterance
- **WHEN** the user holds the button, says "skip the coffee today" and releases
- **THEN** the audio is posted and the screen shows a "listening…" state until the result card appears

#### Scenario: Microphone denied
- **WHEN** microphone permission is denied
- **THEN** the button is replaced by a text field with the same three suggested intents as chips

### Requirement: Transcribe and classify
The system SHALL transcribe the audio with Muse Voice Transcribe and classify it with Muse Spark into exactly one `Intent`: `SkipStop(placeId?)`, `AddStop(placeName, minutes?)`, `RunningLate(minutes)`, `LeavingNow`, `PinMode(mode)`, `ConfirmHabit(habitId)`, `Unknown(text)`.

#### Scenario: Skip stop
- **WHEN** the transcript is "no coffee this morning"
- **THEN** the intent is `SkipStop` with the coffee habit's placeId resolved from the current plan's stops

#### Scenario: Dutch
- **WHEN** the transcript is "ik ga eerst even naar de Albert Heijn"
- **THEN** the intent is `AddStop("Albert Heijn")`

#### Scenario: Unknown
- **WHEN** the transcript does not map to an intent
- **THEN** the intent is `Unknown` and the app shows the transcript with three tappable intents (skip stop, add stop, running late)

### Requirement: Apply intent
A recognised intent SHALL be applied as a plan input change for the current appointment only (never as a change to habits or the calendar), then trigger an agent run.

#### Scenario: Running late
- **WHEN** the intent is `RunningLate(10)`
- **THEN** the planner's current-time offset is set to +10 min for this plan, the agent re-plans, and the Now screen updates

#### Scenario: Confirm habit by voice
- **WHEN** the intent is `ConfirmHabit`
- **THEN** the same `ConfirmHabitHandler` runs as for the buttons; the agent cannot confirm a habit on its own

### Requirement: Result card
The app SHALL show one card with the transcript, the applied change in one line, and an Undo button for 10 seconds.

#### Scenario: Undo
- **WHEN** the user taps Undo
- **THEN** the input change is reverted and the agent re-plans
