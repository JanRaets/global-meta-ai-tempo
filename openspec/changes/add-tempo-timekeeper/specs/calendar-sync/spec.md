# calendar-sync Specification

## ADDED Requirements

### Requirement: Google Calendar read-only connection
The system SHALL let the user connect a Google account with read-only calendar scope via OAuth and SHALL store the refresh token encrypted at rest.

#### Scenario: Successful connect
- **WHEN** the user completes the OAuth consent flow
- **THEN** the token is stored and an initial sync is enqueued

#### Scenario: Scope limited to read
- **WHEN** the consent URL is generated
- **THEN** it requests only `calendar.readonly`

### Requirement: Periodic appointment sync
The system SHALL sync events in the window [now − 1h, now + 7d] into `Appointment` records every 5 minutes and on demand.

#### Scenario: New event appears
- **WHEN** a new event with a location exists in the calendar
- **THEN** an `Appointment` is created with `start`, `end`, `title`, `rawLocation` and a resolved `placeId`

#### Scenario: Event moved
- **WHEN** an existing event's start time changes
- **THEN** the matching `Appointment` is updated and a `AppointmentChanged` domain event is raised

#### Scenario: Event deleted
- **WHEN** an event is removed from the calendar
- **THEN** the matching `Appointment` is deleted and any pending nudge for it is cancelled

### Requirement: Location resolution
The system SHALL geocode an event's location string to a `Place`, reusing an existing `Place` when the geocoded point is within 50 m of one.

#### Scenario: Unresolvable location
- **WHEN** geocoding returns no result
- **THEN** the `Appointment` is stored with `placeId = null` and is excluded from departure planning

#### Scenario: Virtual meeting
- **WHEN** the event has no location but has a conference link
- **THEN** the `Appointment` is stored with `placeId = null` and `isVirtual = true`

#### Scenario: Virtual location words (amended 28 Oct 2026, from real use)
- **WHEN** the event's location is a virtual-meeting word such as "Remote", "online", "Zoom", "Teams" or "Google Meet" (case-insensitive)
- **THEN** it is never geocoded — the `Appointment` is stored with `placeId = null` and `isVirtual = true` (live, "Remote" had geocoded to a shop 130 km away)

### Requirement: No calendar writes
The system SHALL NOT create, update or delete calendar events.

#### Scenario: Write attempted
- **WHEN** any code path attempts a write call against the Calendar API
- **THEN** the integration client throws because no write method is implemented
