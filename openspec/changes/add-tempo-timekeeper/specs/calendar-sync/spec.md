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
The system SHALL geocode an event's location string to a `Place`, reusing an existing `Place` when the geocoded point is within 50 m of one. *(Amended 28 Oct 2026, from real use — "Kampong Hockey, Utrecht" geocoded to a Zeist club: resolution is ranked, not Pelias's first hit.)* Resolution order: (0) a stored `PlaceOverride` for the exact text wins outright; (a) a locality named in the text (the segment after the last comma, or a word matching a candidate locality) constrains the top-10 candidates; (b) within the pool, name-token overlap with the text outranks distance to home; (c) when no focused candidate satisfies the locality, the query is repeated without the home focus and the constraint applied again; (d) only then does the nearest candidate win. Every resolution logs its candidate list and the chosen reason (`PlaceResolutionLog`).

#### Scenario: Named locality beats the home focus
- **WHEN** the text is "Kampong Hockey, Utrecht" and the home-focused top-10 ranks a Zeist club first
- **THEN** the Utrecht candidates are the pool, "Kampong" in the name outranks proximity, and both "Kampong Hockey, Utrecht" and "SV Kampong, Utrecht" land on one `Place` (50 m reuse)

#### Scenario: User correction sticks
- **WHEN** the user corrects an appointment's place from the Today sheet
- **THEN** the appointment re-points and replans, and a `PlaceOverride` (rawText → place) makes every future event with the same text resolve there without asking; Settings lists corrections with remove

#### Scenario: Unresolvable location
- **WHEN** geocoding returns no result
- **THEN** the `Appointment` is stored with `placeId = null` and is excluded from departure planning

#### Scenario: Virtual meeting
- **WHEN** the event has no location but has a conference link
- **THEN** the `Appointment` is stored with `placeId = null` and `isVirtual = true`

#### Scenario: Same location text reuses the same place (amended 28 Oct 2026, from real use)
- **WHEN** an event's location text exactly matches another synced appointment's `rawLocation` that already has a place
- **THEN** that place is reused without geocoding — recurring events stay at one venue, and a manually corrected venue stays corrected

#### Scenario: Virtual location words (amended 28 Oct 2026, from real use)
- **WHEN** the event's location is a virtual-meeting word such as "Remote", "online", "Zoom", "Teams" or "Google Meet" (case-insensitive)
- **THEN** it is never geocoded — the `Appointment` is stored with `placeId = null` and `isVirtual = true` (live, "Remote" had geocoded to a shop 130 km away)

### Requirement: No calendar writes
The system SHALL NOT create, update or delete calendar events.

#### Scenario: Write attempted
- **WHEN** any code path attempts a write call against the Calendar API
- **THEN** the integration client throws because no write method is implemented
