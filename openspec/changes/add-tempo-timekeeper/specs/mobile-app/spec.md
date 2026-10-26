# mobile-app Specification

## ADDED Requirements

### Requirement: Installable mobile PWA
The system SHALL serve a mobile-first progressive web app with a web manifest and service worker, usable in standalone display mode on iOS Safari and Android Chrome.

#### Scenario: Add to home screen
- **WHEN** the user adds the app to their home screen and opens it
- **THEN** it launches without browser chrome and shows the Now screen

### Requirement: Location from the app
The app SHALL request geolocation permission on first open with a one-sentence explanation, and while open SHALL post position updates to `POST /location` at most once per 30 seconds or when the user has moved more than 50 m.

#### Scenario: Permission granted
- **WHEN** the user grants location permission
- **THEN** updates begin and the Now screen shows a small "live" indicator

#### Scenario: Permission denied
- **WHEN** the user denies location permission
- **THEN** the app continues to work from the home place, shows a dismissible notice, and offers demo mode

### Requirement: Now screen
The Now screen SHALL show the next appointment with a dominant countdown to `leaveAt`, the travel mode, and a one-line reason listing travel time and included stops. It SHALL NOT display raw coordinates, plan versions or confidence values.

#### Scenario: Upcoming departure
- **WHEN** the next plan has `leaveAt` 14 minutes from now by bicycle with one coffee stop
- **THEN** the screen leads with "Leave in 14 min", shows a bicycle icon, and the line "18 min by bike + 9 min coffee at Bar Bello"

#### Scenario: No upcoming appointment
- **WHEN** no appointment with a place exists in the next 6 hours
- **THEN** the screen shows the next appointment's time and "nothing to plan yet", never an empty state without context

#### Scenario: Late state
- **WHEN** the plan has `isLate = true` or `leaveAt` is in the past and the user is not at the destination
- **THEN** the screen background changes to the late colour, the headline states how late the user is, and one large button presents the recovery action from the latest `Late` nudge

### Requirement: Today screen
The Today screen SHALL show today's appointments as a vertical timeline with leave time, mode and stops per appointment; appointments without a resolvable place SHALL be shown greyed with "no location".

#### Scenario: Timeline order
- **WHEN** three appointments exist today
- **THEN** they are shown in start-time order with the next one highlighted

### Requirement: Habits screen
The Habits screen SHALL show proposed habits as cards with the proposal sentence, one evidence line, and Confirm / Reject buttons, followed by the list of confirmed habits.

#### Scenario: Confirm from app
- **WHEN** the user taps Confirm
- **THEN** the card moves to the confirmed list immediately and `POST /habits/{id}/confirm` is called; on failure the card returns with an error toast

#### Scenario: Nothing proposed
- **WHEN** no proposed habits exist
- **THEN** the screen shows "Nothing new noticed yet" with a one-line explanation of what TEMPO looks for

### Requirement: Settings screen
The Settings screen SHALL let the user set home address, enabled modes, pinned mode, nudge lead minutes, quiet hours, and toggle demo mode (GPX replay).

#### Scenario: Demo mode on
- **WHEN** demo mode is switched on
- **THEN** live geolocation posting stops and the server starts replaying the demo trail; the Now screen shows a "demo" badge

### Requirement: Voice and capture controls
The Now screen SHALL show a hold-to-talk button and a camera button side by side below the countdown, each at least 64 px tall, with behaviour specified in `voice-input` and `capture-appointment`.

#### Scenario: Controls present
- **WHEN** the Now screen is shown in the normal state
- **THEN** both buttons are visible without scrolling on a 390×844 viewport

### Requirement: Trace sheet
Every nudge in the app SHALL have a "Why?" affordance that opens a bottom sheet listing the agent's steps for that run as short plain-language lines, with the provider that served it, and never raw JSON.

#### Scenario: Why opened
- **WHEN** the user taps Why? on a nudge
- **THEN** the sheet shows the steps and a "Served by Muse Spark" footer

### Requirement: Push subscription
The Settings screen SHALL have an "Enable notifications" control; on iOS, when the app is not installed to the home screen, it SHALL show a one-line install hint instead. The subscription is posted to `POST /push/subscribe`, and a test-notification button sends one.

#### Scenario: Notification tap
- **WHEN** the user taps a nudge notification
- **THEN** the app opens on the linked screen (`/now` for nudges, `/habits` for proposals)

### Requirement: In-app nudge mirror
The Now screen SHALL show the last three push-notified nudges, newest first.

#### Scenario: Nudge arrives
- **WHEN** a nudge is sent while the app is open
- **THEN** it appears in the feed within 15 seconds without reload

### Requirement: One-tap interactions
Every action outside the Settings screen SHALL complete with a single tap and no text input.

#### Scenario: Recovery action
- **WHEN** the user taps the late-state recovery button
- **THEN** the chosen alternative is applied to the plan (stop skipped or mode switched) and the screen returns to the normal state with the updated countdown
