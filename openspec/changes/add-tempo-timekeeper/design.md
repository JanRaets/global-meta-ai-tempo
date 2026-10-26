# Design: TEMPO

## Context
Hackathon MVP, single user. Building period 27 Oct – 4 Nov 2026, submission 4 Nov 21:00 ET (public repo + 3-minute video). Fresh build from an empty repo. Demo surface is a mobile-first PWA on the presenter's phone; Telegram is the push channel.

### Judging, and what this design optimises for
| Criterion | Weight | What in this design answers it |
|---|---|---|
| Impact | 30% | A real daily problem; learns from a week of the builder's own movement, not seeded data |
| Use of Meta AI tooling | 25% | Spark as the agent (tool loop + judgement), Spark vision for capture, Voice Transcribe for hands-free input, Spark Open on Baseten as a verified open-weight path, Muse Code in the build |
| Technical depth | 20% | Deterministic tools + model judgement, type-enforced approval, recorded agent traces, tests on every tool |
| Demo | 25% | One phone, one walk, one voice command, one photo; a trace view that shows the agent thinking |

### Demo principle: keep it simple
One phone, one calendar, one walk. The video is: open the app → "leave in 14 min, with coffee" → say "skip the coffee today" → countdown jumps → photo a WhatsApp invite → it appears in Today → walk out late → "take the train instead" → show the trace of what the agent did. Everything in this design exists to make that loop reliable.

## Goals
- A real appointment produces a correctly-timed leave-now nudge.
- Recurring stops are inferred from the builder's real location history over the building period and, once confirmed, change the leave-now time.
- Spoken and photographed input change the plan with one tap.
- When the user is late, the agent proposes one concrete recovery and can explain why.

## Non-Goals
- Native mobile app, multi-tenant, billing, writing to the calendar, background location, SAM / image generation (not useful here; don't bolt them on for points).

## Decisions

### D1 — Tools compute, the model decides
All time computation, routing and clustering is deterministic C# exposed as tools. Muse Spark runs a tool-calling loop: it reads state through tools, asks the planner for options, picks one, and writes the message. The model never emits a time, duration or coordinate as a fact; any such value in its output must be a reference to a tool result. Rationale: correctness and testability, and a real agentic use (not a text generator behind a form) for the judges.

### D2 — Single `IMetaModels`, Meta only
```
IMetaModels
  ChatAsync(messages, tools, options)        → assistant turn with optional tool calls   (Spark / Spark Open)
  ChatJsonAsync<T>(messages, schema)         → typed object, one retry                   (Spark / Spark Open)
  DescribeImageAsync<T>(image, schema)       → typed object                              (Spark vision)
  TranscribeAsync(audio)                     → text                                      (Voice Transcribe)
```
Two implementations: `MetaApiModels` (Muse Spark + Voice Transcribe over Meta Model API) and `BasetenModels` (Muse Spark Open `meta-models/muse-spark-open-1026`; transcription delegates to Meta API). `Models:Primary = MetaApi | Baseten`, with automatic fallback to the other on 429/5xx. No other provider SDK in the repo. Rationale: hackathon requirement; fallback protects the demo from credit delays and rate limits.

### D3 — Type-enforced approval
`Habit` has `State ∈ {Proposed, Confirmed, Rejected}`. `ConfirmedHabit` has an `internal` constructor invoked only by `ConfirmHabitHandler`. `DeparturePlanner.Plan(...)` accepts `IReadOnlyList<ConfirmedHabit>`. A test asserts the constructor is not public. The agent cannot confirm a habit; only the user can, via Telegram button, app button or an explicit voice intent that is itself routed through the same handler.

### D4 — Raw location is ephemeral
`POST /location` writes `{lat,lng,ts}` to a Redis list keyed by day with 24h TTL. `CollapseVisitsJob` (every 10 min) detects stays (≥ 5 min within 75 m), reverse-geocodes the centroid to a `Place`, persists a `Visit`. Nothing else persists raw points.

### D5 — Habit = place + context, inferred by thresholds, worded by Spark
Context key: `(weekday, timeBucket[early|morning|midday|afternoon|evening], direction[toward-home|away-from-home|other], adjacentAppointmentKind)`. Candidate when the same `(placeId, contextKey)` has ≥ 3 Visits and duration stddev ≤ 8 min. Spark produces the proposal text and a one-line rationale from the structured facts via `ChatJsonAsync`. The builder runs the app on their own phone from day 1 so real habits exist by the video.

### D6 — Planner tool
`plan.options(appointmentId)` returns, for each enabled mode and for each subset of matching confirmed habits (all stops / each stop skipped / no stops), a `PlanOption { mode, stops, leaveAt, arriveAt, isFeasible, slackMin }`, computed as:
```
baseTravel = routing.estimate(current, A.place, m, now)
stopTime   = Σ stops.avgDuration
detour     = routing.detour(current → stops… → A.place, m) − baseTravel
leaveAt    = A.start − A.bufferMin − baseTravel − stopTime − detour
```
`plan.commit(optionId, reason)` persists the chosen option as the current `DeparturePlan` version with the agent's reason. The agent chooses; the tool computes. Plan version increments on any input change; nudges are keyed by `(planId, version)`.

### D7 — Agent loop
Triggered by: planner tick (every 2 min while an appointment is within 6 h), `AppointmentChanged`, `VisitCreated`, habit confirmed, settings changed, a voice intent, a captured appointment. One run:
1. System prompt states the rules (tools compute; never invent times; one recovery option; ≤ 2 sentences; respect quiet hours) and the user's standing preferences.
2. Tools available: `calendar.upcoming`, `location.current`, `plan.options`, `plan.commit`, `plan.current`, `habits.confirmed`, `notify.send`, `trace.note`.
3. The loop runs until the model stops calling tools or hits 8 calls. Every call, result and the final assistant message are appended to an `AgentTrace`.
4. Guard: `notify.send` is refused by the tool if a nudge for `(planId, version, kind)` already exists or quiet hours apply (except the late exception). The guard lives in the tool, not the prompt.
Rationale: the agent's autonomy is bounded by tool guards, which is both safer and the "verifiable agent" story.

### D8 — Nudge policy (enforced in tools)
- Leave nudge at `leaveAt − nudgeLeadMin` (default 10). One per plan version.
- Late: when `now > leaveAt` and user not at destination, the agent is invoked with `plan.options` already including the current state; it must commit exactly one recovery option and send one `Late` nudge.
- Quiet hours suppress everything except a late nudge for an appointment within 60 min.
- Habit proposals batched to one message per day.

### D9 — Voice input
PWA hold-to-talk records ≤ 15 s (MediaRecorder, webm/opus), posts to `POST /voice`. Server: `TranscribeAsync` → `ChatJsonAsync<Intent>` where `Intent` is one of `SkipStop(placeId?)`, `AddStop(placeName, minutes?)`, `RunningLate(minutes)`, `LeavingNow`, `PinMode(mode)`, `Unknown(text)`. A recognised intent is applied as a plan input change (skipped stop for this plan, extra stop, etc.) and triggers an agent run; the app shows the transcript and the resulting change in one card. `Unknown` shows the transcript and asks for a tap on one of three likely intents. Rationale: one tap, hands-free, shows Voice Transcribe + Spark working together; intents are a closed set so the model cannot do anything the UI couldn't.

### D10 — Capture appointment
PWA camera button → photo → `POST /capture`. Server: `DescribeImageAsync<CapturedAppointment { title, dateTime, placeText, confidence, rawText }>`; the place is geocoded through the same path as calendar events. The app shows a confirm card (title, time, place, with the photo thumbnail); on confirm, an `Appointment { source = Captured }` is created locally (never written to Google Calendar). Rationale: the "surprise" moment in the demo, and a real daily need (school letters, WhatsApp plans); it reuses calendar-sync's downstream path entirely.

### D11 — Location sources
Three sources, all posting to the same `POST /location`, selected per device:
- The PWA: `navigator.geolocation.watchPosition` while open (foreground only). Demo surface.
- Owntracks (App Store / Play, free) in HTTP mode pointed at `POST /location/owntracks` with the per-user key, for all-day background tracking during the building period so real Visits accumulate. The endpoint accepts Owntracks' JSON (`_type: location`, `lat`, `lon`, `tst`, `acc`) and maps it to the standard point.
- `GpxReplaySource` for tests and the demo-mode toggle in Settings.
Rationale: PWAs cannot track in the background on iOS; Owntracks is a 30-minute adapter that gives a real week of data without a native build.

### D12 — Mobile PWA is the only UI
Screens: **Now** (countdown, reason line, hold-to-talk and camera buttons, recent nudges), **Today** (timeline), **Habits** (proposed/confirmed), **Settings**, plus a **Trace** sheet reachable from any nudge ("why?") that lists the agent's tool calls in plain language. No desktop dashboard.

### D13 — UX rules
- One number dominates each screen. On **Now** that is "leave in N min".
- Never show raw data the user must interpret. Show the decision and the reason in one line.
- Habit proposals read like a friend noticing something; evidence in one line; two large buttons.
- Late state changes the whole screen colour and leads with the single recovery action.
- Voice and camera are one hold / one tap; the result is a card the user confirms or dismisses.
- "Why?" on any nudge opens the trace: tool calls rendered as short sentences, never JSON.
- Settings is the only screen with inputs.

### D14 — Video and submission
Recorded from the phone (screen recording + a second phone for the walk), 3 minutes, following the demo loop above, ending with the trace sheet. README has: what was built during the hackathon (everything), which Meta tools and where (table), how to run, and the Muse Code sections. Repo public from first commit.

### D15 — Routing provider: OpenRouteService (decided 26 Oct 2026)
OpenRouteService is the routing provider (`IRoutingClient` in `Tempo.Integrations.Maps`). Rationale: free, and bike/walk/car are what the demo needs; transit is out of scope for the hackathon. Section 4 implements the ORS Directions API with profiles `cycling-regular` (bike), `foot-walking` (walk) and `driving-car` (car); geocoding uses ORS's Pelias geocode endpoints under the same key. Mode `train` stays in the mode enum but is disabled by default in `UserSettings`, and `plan.options` skips disabled modes. `Routing:*` config is the ORS base URL + API key. Also recorded here: Meta Model API credits are live, so section 1 builds `MetaApiModels` first and `BasetenModels` second as the fallback (`Models:Primary = MetaApi`).

## Risks / Trade-offs
- Meta API credits may arrive late. Mitigation: Baseten path first, API path second; `IMetaModels` makes the swap a config change.
- Rate limits on the Meta API during a 2-minute tick loop. Mitigation: the tick only invokes the agent when an input changed since the last run; otherwise the committed plan stands.
- Spark Open on Baseten may differ in tool-calling reliability from Spark. Mitigation: the scripted-fake agent tests pin the expected sequences; a "tool-call sanity" integration test runs against both providers in CI (skipped without keys).
- Routing transit quality in NL varies; resolved by D15: transit is out of scope, ORS covers bike/walk/car for free.
- Foreground-only location: nudges when the app is closed depend on Telegram push. Acceptable.
- Tunnel is a single point of failure for the live demo. Mitigation: demo mode (GPX replay) and the video are the primary artefacts; live is a bonus.
- Vision extraction from photos is fuzzy. Mitigation: user always confirms; the confirm card is the feature, not the extraction accuracy.

## Migration Plan
Greenfield. EF Core migrations from day one; `dotnet ef database update` in compose startup.

## Open Questions
None. The one blocking decision (routing provider) is resolved in D15.
