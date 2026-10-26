# Change: Add TEMPO proactive time-keeper

## Why
Calendar apps tell you when something starts, not when you need to leave. Users are late not because they forget appointments but because they misjudge travel time and forget the small stops they always make on the way. TEMPO closes that gap with an agent that plans departures, learns the user's habits, and can be told or shown things in the moment. Hackathon entry: Meta Global AI Developer Hackathon, Indie Developer track, "Build for Your World". Submission due 4 Nov 2026 21:00 ET.

## What Changes
- **ADD** `meta-models`: single `IMetaModels` abstraction with Muse Spark (Meta Model API) and Muse Spark Open (Baseten) implementations, plus Muse Voice Transcribe
- **ADD** `agent-loop`: Muse Spark tool-calling loop over deterministic tools; produces plans, nudges and recovery decisions; every run recorded as an `AgentTrace`
- **ADD** `calendar-sync`: Google Calendar read-only sync into `Appointment` records with resolved `Place`
- **ADD** `location-ingest`: location point ingestion, 24h Redis buffer, collapse into `Visit` records
- **ADD** `departure-planning`: deterministic planner tool producing `DeparturePlan` from appointment, location, routing and confirmed habits
- **ADD** `habit-learning`: nightly clustering of Visits into candidate habits, Spark-worded proposals, Telegram confirm/reject, type-enforced confirmed-only usage
- **ADD** `nudging`: leave-now and late-recovery nudges over Telegram with de-duplication and quiet hours
- **ADD** `voice-input`: hold-to-talk in the PWA → Muse Voice Transcribe → Spark turns the utterance into a structured `Intent` → plan adjusted
- **ADD** `capture-appointment`: photo of an invite / message / newsletter → Spark vision extracts an appointment → user confirms → treated like a synced appointment
- **ADD** `mobile-app`: mobile-first PWA — the demo surface. Now, Today, Habits, Settings, plus hold-to-talk and capture buttons; posts the phone's location

## Impact
- Affected specs: all new (no existing specs in this repo)
- Affected code: new solution `Tempo.sln` with `Tempo.Api`, `Tempo.Core`, `Tempo.Agent`, `Tempo.Integrations.*`, `Tempo.Jobs`, `Tempo.Web` (PWA)
- External: Meta Model API key and credits, Baseten key, Google Calendar OAuth consent, routing provider key, Telegram bot token, public HTTPS endpoint

## Decided
- Fresh build. The repo is created empty on 27 Oct 2026. Patterns are reimplemented, no code imported.
- Models: Muse Spark primary, Muse Spark Open on Baseten as fallback and for the open-weight bonus; Muse Voice Transcribe for speech. No Llama, no other providers.
- Location sources: the PWA via browser Geolocation while open (demo), Owntracks in HTTP mode for all-day background tracking during the building period, and `GpxReplaySource` for tests and demo mode.
- Meta Model API credits are live ($150). Section 1 builds `MetaApiModels` first, `BasetenModels` second as fallback.
- Demo deliverable: a 3-minute video recorded from the phone plus a short screen-recorded trace view; last two days of the building period are reserved for it.
- Tooling: built mostly with Claude Code; Muse Code is used for section 11 (capture-appointment) only, and if that doesn't happen the README says so. The README's tooling line stays honest either way.
- Routing provider: OpenRouteService (free; bike/walk/car are what the demo needs; transit out of scope). Recorded as design D15.

## Decisions required before Phase 0 (BLOCKING — escalate to human, do not guess)
None — all resolved above.
