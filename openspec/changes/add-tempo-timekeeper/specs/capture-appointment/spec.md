# capture-appointment Specification

## ADDED Requirements

### Requirement: Capture button
The app SHALL provide a camera button on the Now screen that takes or picks a photo and posts it to `POST /capture`.

#### Scenario: Photo taken
- **WHEN** the user takes a photo of a message that says "Dinner Thursday 19:00 at De Jonge Haan"
- **THEN** the photo is posted and a confirm card appears within 10 seconds

### Requirement: Extract with Spark vision
The system SHALL call `DescribeImageAsync<CapturedAppointment>` to extract `title`, `dateText` and `timeText` (the date/time words literally as written — the model never resolves a date, per design D1), `placeText`, `confidence` and `rawText`; a deterministic resolver SHALL turn the words into a start time relative to today in the user's timezone, and the system SHALL geocode `placeText` through the same path as calendar locations. *(Amended 27 Oct 2026: `dateTime` split into literal `dateText`/`timeText` + code-side resolution, so no model output is ever used as a time — D1 enforced in the schema, not the prompt.)*

#### Scenario: Relative date
- **WHEN** the image says "Thursday 19:00" and today is Monday
- **THEN** `dateText` is "Thursday", `timeText` is "19:00", and the resolved start is the coming Thursday at 19:00 local time

#### Scenario: No appointment found
- **WHEN** the image contains no date or place
- **THEN** the card says what was read and offers to retake

### Requirement: User confirms
A captured appointment SHALL become an `Appointment { Source = Captured }` only when the user confirms the card; it SHALL NOT be written to Google Calendar.

#### Scenario: Confirm
- **WHEN** the user taps Add
- **THEN** the appointment appears in Today and the agent plans for it like any other

#### Scenario: Edit before confirm
- **WHEN** the user taps the time on the card
- **THEN** a native time picker lets them correct it before adding

### Requirement: Captured appointments are first-class
Captured appointments SHALL flow through departure planning, nudging and habit context exactly like synced ones.

#### Scenario: Nudge for captured
- **WHEN** a captured appointment is next
- **THEN** a leave nudge is produced for it
