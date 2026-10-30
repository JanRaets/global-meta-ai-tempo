# Design: TEMPO

## Context
Hackathon MVP, single user. Building period 27 Oct – 4 Nov 2026, submission 4 Nov 21:00 ET (public repo + 3-minute video). Fresh build from an empty repo. Demo surface is a mobile-first PWA on the presenter's phone; Web Push to the installed PWA is the notification channel (D16).

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

**Meta Model API facts (verified against dev.meta.ai/docs and the live API, 26 Oct 2026):**
- Base URL `https://api.meta.ai/v1`, auth `Authorization: Bearer <key>`.
- Chat: `POST /v1/chat/completions`, OpenAI-compatible wire format. Model id **`muse-spark-1.3`** (tool calling, image understanding, structured output). `tools` with `type:"function"`; `tool_choice` supports only `"auto"`; the assistant message incl. its `tool_calls` must be echoed back before `role:"tool"` results (else 400).
- Structured output: `response_format: { type:"json_schema", json_schema:{ name, schema, strict? } }`; output lands in `choices[0].message.content` as a JSON string. `strict:true` requires `additionalProperties:false` + all-props-required; we send `strict:true` whenever the schema meets that contract (a lax schema stays `strict:false`). *(Amended 27 Oct: lax mode occasionally emitted a corrupted property key — observed live: `"placeName promenade"` — which strict decoding never does; the intent classifier was the first strict schema.)*
- Transcription: `POST /v1/asr/transcribe`, model **`muse-voice-transcribe-1.0`**, multipart with a JSON `request` part (`model`, `audioEncoding:"WAV"`, `mode:"PUSH_TO_TALK"`, optional `keywords`/`languageBias`) and an `audio` part. **WAV only** (RIFF, mono integer PCM, 16/24 kHz), ≤10 min, ≤32 MB. Response `{ sessionId, transcript, audioDurationMs, turns }` — **no detected-language field**.
- Muse Spark Open is open-weight, not on the Model API; served via hosted providers (our path: Baseten, base URL `https://inference.baseten.co/v1`, model `meta-models/muse-spark-open-1026`, `Bearer` auth). *(Verified live 28 Oct 2026:)* Baseten validates function names against `[a-zA-Z0-9_-]` and 400s on our canonical dotted tool names (`calendar.upcoming`); `BasetenModels` maps `.` ↔ `_` at the wire boundary only — registry, prompts and traces keep the dots (the swap is bijective because no tool name contains an underscore). Tool-call arguments and structured output came back clean in 29/29 live calls — no reasoning text leaked into content.

### D3 — Type-enforced approval
`Habit` has `State ∈ {Proposed, Confirmed, Rejected}`. `ConfirmedHabit` has an `internal` constructor invoked only by `ConfirmHabitHandler`. `DeparturePlanner.Plan(...)` accepts `IReadOnlyList<ConfirmedHabit>`. A test asserts the constructor is not public. The agent cannot confirm a habit; only the user can, via the app's Confirm button, a notification action (Android) or an explicit voice intent — all routed through the same handler.

### D4 — Raw location is ephemeral
`POST /location` writes `{lat,lng,ts}` to a Redis list keyed by day with 24h TTL. `CollapseVisitsJob` (every 10 min) detects stays (≥ 5 min within 75 m), reverse-geocodes the centroid to a `Place`, persists a `Visit`. Nothing else persists raw points.

### D5 — Habit = place + context, inferred by thresholds, worded by Spark
Context key: `(weekday, timeBucket[early|morning|midday|afternoon|evening], direction[toward-home|away-from-home|other], adjacentAppointmentKind)`. *(Defined 27 Oct: adjacentAppointmentKind is the closed set `meeting|personal|medical|travel|unknown`, classified deterministically from the appointment title (`AppointmentKind.Classify`); a visit with no appointment starting within 2 h after its arrival keeps `null`, which matches any kind. Direction is derived from surrounding home visits: home visit ending ≤3 h before → away-from-home; home visit starting ≤3 h after → toward-home; else other.)* Candidate when the same `(placeId, contextKey)` has ≥ 3 Visits and duration stddev ≤ 8 min. *(Planner matching note, 27 Oct: a stop on the way happens shortly before the appointment, so a habit also matches when its time bucket is the one preceding the appointment's — 08:50 coffee before a 09:30 meeting.)* Spark produces the proposal text and a one-line rationale from the structured facts via `ChatJsonAsync`. The builder runs the app on their own phone from day 1 so real habits exist by the video. *(Amended 29 Oct 2026: a context's weekday may be null, meaning "any Monday–Friday" — matching only; inference still derives concrete days. Exists for seeded/hand-edited habits like the demo's weekday-morning coffee stop.)* *(Amended 30 Oct 2026 — chained-origin amendment: habits carry a `Kind` — `Stop` joins the route; `ReturnsHome`/`StaysPut` decide a look-ahead plan's origin and never enter stop subsets. Their context gains a `gapBucket` (`short` < 1 h, `medium` 1–3 h, `long` ≥ 3 h) and matches on previous-appointment kind + weekday + gap bucket. Evidence: a home Visit between two same-day placed appointments (ReturnsHome) or staying at the previous place until the next departure (StaysPut), on ≥ 3 comparable days. Proposed, confirmed and guarded like every other habit.)*

### D6 — Planner tool
`plan.options(appointmentId)` returns, for each enabled mode and for each subset of matching confirmed habits (all stops / each stop skipped / no stops), a `PlanOption { mode, stops, leaveAt, arriveAt, isFeasible, slackMin }`, computed as:
```
baseTravel = routing.estimate(current, A.place, m, now)
stopTime   = Σ stops.avgDuration
detour     = routing.detour(current → stops… → A.place, m) − baseTravel
leaveAt    = A.start − A.bufferMin − baseTravel − stopTime − detour
```
`plan.commit(optionId, reason)` persists the chosen option as the current `DeparturePlan` version with the agent's reason. The agent chooses; the tool computes. Plan version increments on any input change; nudges are keyed by `(planId, version)`.

**Phase model (amended 27 Oct 2026, from real use):** a plan carries `PlanPhase { Upcoming, Late, EnRoute, Arrived }`, evaluated deterministically on every tick and every accepted location post — never by the model. `Upcoming → Late` when now passes `leaveAt` (the current fix, or home, is snapshotted as the leave reference). `→ EnRoute` from Upcoming or Late when the user has moved more than `Phase:EnRouteMeters` (default 150) from that reference — **only once `leaveAt` has passed** (amended 28 Oct 2026, from real use: before `leaveAt` the only reference is home, and ordinary pre-departure movement — or an imprecise home coordinate — must not read as a departure; an early leaver flips to EnRoute at `leaveAt`, when the reference snapshots at their current position and the next fix shows further movement). `→ Arrived` when within `Phase:ArrivedMeters` (default 100) of the destination, or at `start + Phase:ArrivedFallbackMin` (default 15) as a fallback; Arrived clears the plan from Now and the next appointment takes over. Thresholds are forgiving because Owntracks significant mode is coarse; the PWA's `watchPosition` posts are the primary signal while open. While Late or EnRoute, the tick re-runs the planner deterministically and refreshes the committed option's `leaveAt`/`arriveAt` **in place** for the same mode and stops — same version, no new nudge, no model call; only when a different option arrives more than 5 minutes earlier than the refreshed choice does the agent run again (that is an input change). Phase transitions are recorded as plain-language trace rows (`trigger "phase"`), excluded from the daily run cap. **Phase is journey state, not plan-version state (amended 28 Oct 2026, from real use):** a recommit carries `Phase`, `PhaseChangedAt` and the leave fix forward onto the new version — a mid-drive recommit had reset them and let a second "leave now" nudge through. While EnRoute or Arrived, the tick never wakes the model for changed inputs (the user's own movement changes them every tick); only a better alternative or a routing recovery does. **AtDestination and chained origins (amended 28 Oct 2026, from real use):** a fresh fix within `Phase:ArrivedMeters` of an upcoming appointment's place before it starts makes the plan `AtDestination` — one zero-travel option (leaveAt = start, no stops, habits unmatched), committed deterministically ("already at destination"), every nudge refused, Now shows "You're here"; a later fix beyond the radius before start reverts to Upcoming and planning resumes. Look-ahead plans chain: only the next placed appointment departs from the current fix; a later one considers the previous placed appointment. **Chained origin assumes home (amended 30 Oct 2026, from real use: a 20:00 party was planned from hockey ending 17:00 — the flat 3 h window was too generous):** the look-ahead plan departs from the previous place only when the gap is shorter than the travel home from there plus `Phase:StayThresholdMin` (default 45); otherwise home. Overlapping/back-to-back events always chain; gaps ≥ 6 h always assume home. The home leg rides the cached routing, straight-line when unavailable. Evidence wins: a fresh fix at home names the origin home, a fresh fix still at the previous place after its end names that place. Confirmed `ReturnsHome`/`StaysPut` habits (D5) override the heuristic. Options and plans carry `originKind`/`originName`, every chained Today row names its origin ("from home" / "from <title>" / "from here"), same-place pairs show "no travel", and habit matching uses the chained origin. **The buffer is a margin, not a deadline (amended 28 Oct 2026, from real use):** every option and plan carries `leaveAt` (start − travel − stops − buffer: the target, driving nudges and the countdown) and `leaveBy` (start − travel − stops: the last on-time moment); `isFeasible` means `arriveAt ≤ start` and `slackMin` counts to `leaveBy`. Between them the phase is `LeaveNow` — "Leave now, you'll still arrive on time", normal colours, one gentle `LeaveNow` nudge per plan version at `leaveAt` (guard in `notify.send`). `Late` begins only past `leaveBy` (unless EnRoute), and the late headline counts minutes past `leaveBy` — the minutes the arrival actually slips. **Ranked geocoding and place corrections (amended 28 Oct 2026, from real use):** resolution is override-first, then locality-constrained and name-overlap-ranked over the top-10 Pelias candidates (requeried without the home focus when the focused list has no in-locality candidate; nearest only as the last resort), each resolution logged with its candidates and reason; the Today row's place line opens "Is this the right place?" (logged top-3 + search), and a pick re-points the appointment, replans, and stores a sticky `PlaceOverride`; a one-off deploy job re-resolves the next 7 days. **Past appointments show the outcome (amended 28 Oct 2026, from real use):** an ended appointment is never planned again (options frozen, no routing); its plan stores a once-derived outcome — `leftAt` from the EnRoute trace or the first moved fix, `arrivedAt` from the by-proximity Arrived trace or the overlapping Visit (the start+15 fallback is not evidence) — rendered as "Left 08:11 · arrived 08:18 · on time" on greyed Today rows, with a single end-of-day summary line ("3 trips · 2 on time · 1 late by 6 min"), no streaks, no scores.

### D7 — Agent loop
Triggered by: planner tick (every 2 min while an appointment is within 6 h), `AppointmentChanged`, `VisitCreated`, habit confirmed, settings changed, a voice intent, a captured appointment. One run:
1. System prompt states the rules (tools compute; never invent times; one recovery option; ≤ 2 sentences; respect quiet hours) and the user's standing preferences.
2. Tools available: `calendar.upcoming`, `location.current`, `plan.options`, `plan.commit`, `plan.current`, `habits.confirmed`, `notify.send`, `trace.note`.
3. The loop runs until the model stops calling tools or hits 8 calls. Every call, result and the final assistant message are appended to an `AgentTrace`.
4. Guard: `notify.send` is refused by the tool if a nudge for `(planId, version, kind)` already exists or quiet hours apply (except the late exception). The guard lives in the tool, not the prompt.
Rationale: the agent's autonomy is bounded by tool guards, which is both safer and the "verifiable agent" story.

### D8 — Nudge policy (enforced in tools)
- Nudges are delivered by `INudgeTransport = WebPushTransport` (D16).
- Leave nudge at `leaveAt − nudgeLeadMin` (default 10). One per plan version. *(Amended 27 Oct: the window check is enforced inside `notify.send` too — a Leave nudge before `leaveAt − nudgeLeadMin` is refused — so the tick's timing and the tool's guard agree. Late nudge text names the recovery's arrival time, never a leave time, which would already be in the past.)* *(Amended 29 Oct 2026 — replacement carries its cause: plans track `ShiftReason`/`shiftedFromLeaveAt` when `leaveAt` moves (Traffic when the delay grew ≥3 min and explains ≥ half the shift); when the near-departure refresh moves `leaveAt` >5 min earlier after the Leave nudge went out, one replacement per appointment is sent — earlier shifts only, Upcoming only, window open — whose text must name the cause in plain words with both times plan values; guards in `notify.send`, trace records "replan: traffic +7 min". See nudging spec.)*
- Late: when `now > leaveAt` and user not at destination, the agent is invoked with `plan.options` already including the current state; it must commit exactly one recovery option and send one `Late` nudge.
- Quiet hours suppress everything except a late nudge for an appointment within 60 min.
- **No nudges once en route (amended 27 Oct 2026):** when the plan phase is EnRoute or Arrived, `notify.send` refuses Leave and Late nudges and the tick does not wake the agent for them — the user is already moving; nagging them is the one thing TEMPO never does (D13).
- Habit proposals batched to one message per day.

### D9 — Voice input
PWA hold-to-talk records ≤ 15 s as WAV (Web Audio capture, mono PCM 16 kHz — the ASR API accepts WAV only, see D2 API facts), posts to `POST /voice`. Server: `TranscribeAsync` → `ChatJsonAsync<Intent>` where `Intent` is one of `SkipStop(placeId?)`, `AddStop(placeName, minutes?)`, `RunningLate(minutes)`, `LeavingNow`, `PinMode(mode)`, `Unknown(text)`. A recognised intent is applied as a plan input change (skipped stop for this plan, extra stop, etc.) and triggers an agent run; the app shows the transcript and the resulting change in one card. `Unknown` shows the transcript and asks for a tap on one of three likely intents. Rationale: one tap, hands-free, shows Voice Transcribe + Spark working together; intents are a closed set so the model cannot do anything the UI couldn't.

### D10 — Capture appointment
PWA camera button → photo → `POST /capture`. Server: `DescribeImageAsync<CapturedAppointment { title, dateText, timeText, placeText, confidence, rawText }>` — the model returns the date/time words literally as written, and a deterministic resolver (EN/NL day names, relative words, explicit day-first dates, 24h/`19u30` times) computes the start in the user's timezone, so no model output is ever a resolved time (D1; amended 27 Oct 2026 from a model-resolved `dateTime`); the place is geocoded through the same path as calendar events. The app shows a confirm card (title, time, place, with the photo thumbnail); on confirm, an `Appointment { source = Captured }` is created locally (never written to Google Calendar). Rationale: the "surprise" moment in the demo, and a real daily need (school letters, WhatsApp plans); it reuses calendar-sync's downstream path entirely. **Resolve once (amended 29 Oct 2026, from real use:** the card showed "De Jonge, Leiderdorp" and confirm re-geocoded the same text to "De Jonge Graaf, Cothen"**):** the place is geocoded once at capture; the card's coordinates travel back with the confirm and become the appointment's place verbatim — no second geocode, no >50 km retry (the capture geocode already ran home-focused). Only an edited place text re-geocodes, through the old path.

### D11 — Location sources
Three sources, all posting to the same `POST /location`, selected per device:
- The PWA: `navigator.geolocation.watchPosition` while open (foreground only). Demo surface.
- Owntracks (App Store / Play, free) in HTTP mode pointed at `POST /location/owntracks` with the per-user key, for all-day background tracking during the building period so real Visits accumulate. The endpoint accepts Owntracks' JSON (`_type: location`, `lat`, `lon`, `tst`, `acc`) and maps it to the standard point.
- `GpxReplaySource` for tests and the demo-mode toggle in Settings.
Rationale: PWAs cannot track in the background on iOS; Owntracks is a 30-minute adapter that gives a real week of data without a native build.

**Demo arc (amended 29 Oct 2026, from two demo walks whose trails left home inside the margin and skipped the Late beat):** the demo appointment is "Demo: meeting met Tom" at today 10:00 when that is 20–75 min out (the morning filming scene: "Leave in N min" with the coffee stop), otherwise now+30; an existing placed appointment within 60 min becomes the arc instead. The replayed trail is adaptive, not fixed-length: it jitters at home until two minutes past the committed plan's `leaveBy` **and** the Late nudge has been sent (giving up 6 min past `leaveBy`, or after 25 min), then drives a ~3.5-minute GPX leg to the destination — so the arc is always Upcoming → LeaveNow → Late (nudge) → EnRoute → Arrived. The thin-data seed creates the two demo habits directly: Bar Bello CONFIRMED on any weekday morning before a meeting (9 min, away from home — `ContextKey` with null weekday, which matches Monday–Friday), and Albert Heijn PROPOSED for Thursday evenings, so the confirm flow stays demonstrable. While demo mode is on, Demo nudges show in the Recent feed (they die with the toggle).

### D12 — Mobile PWA is the only UI
Screens: **Now** (countdown, reason line, hold-to-talk and camera buttons, recent nudges), **Today** (timeline), **Habits** (proposed/confirmed), **Settings**, plus a **Trace** sheet reachable from any nudge ("why?") that lists the agent's tool calls in plain language. *(Amended 29 Oct 2026: en route, Now keeps normal colours with minutes-to-arrival dominant; a projected arrival past the start turns the arrival chip — only the chip — amber: "arriving 08:24 · 4 min late". No nudge. Rationale: calm while driving — a red screen or a push adds nothing the driver can act on.)* No desktop dashboard. Every action lives in the app; a notification tap deep-links to `/now` (nudges) or `/habits` (proposals). On Android, proposal notifications carry Confirm/Reject actions that call the same handlers; iOS has no notification actions.

### D13 — UX rules
These rules exist to minimise cognitive load for people who find time hard (project.md Purpose): the app answers one question per screen and never makes the user do time arithmetic.
- One number dominates each screen. On **Now** that is "leave in N min".
- No countdown anxiety: the dominant number is minutes-to-leave, never seconds, and it changes colour only at the late threshold. *(Amended 29 Oct 2026: while EnRoute with projected arrival past the start, the arrival chip — never the whole screen — turns amber; see D12.)*
- Nudges are phrased as the next physical action ("grab your keys, bike's 18 min"), not as time arithmetic the user must verify.
- Nothing nags: one nudge per plan version, one late nudge, no reminders of reminders — enforced by the duplicate guard in `notify.send` (D8) and the tick's no-rewake check (D7), not by UI convention.
- Never show raw data the user must interpret. Show the decision and the reason in one line.
- Habit proposals read like a friend noticing something; evidence in one line; two large buttons.
- Late state changes the whole screen colour and leads with the single recovery action.
- Voice and camera are one hold / one tap; the result is a card the user confirms or dismisses.
- "Why?" on any nudge opens the trace: tool calls rendered as short sentences, never JSON.
- Settings is the only screen with inputs.

### D14 — Video and submission
Recorded from the phone (screen recording + a second phone for the walk), 3 minutes, following the demo loop above, ending with the trace sheet. README has: what was built during the hackathon (everything), which Meta tools and where (table), how to run, and the Muse Code sections. Repo public from first commit.

### D15 — Routing providers: OpenRouteService + TomTom for car traffic (amended 28 Oct 2026)
*(Amendment, 28 Oct 2026:)* ORS has no live traffic, so car estimates move to TomTom Routing (`CompositeRoutingClient`: car → `TomTomRoutingClient` with `traffic=true` and `departAt`, ORS as in-flight fallback on any failure/missing key/spent quota; bike/walk stay on ORS; `Routing:Car:Provider` switches back to `Ors`). `travelTimeInSeconds` includes the live delay; `trafficDelayInSeconds` is surfaced as `RouteEstimate.TrafficDelay` → `PlanOption.TrafficDelayMin` → the committed plan, shown as "+N traffic" from 5 min up and given to the agent as a fact (it may say traffic is bad, never the number). Same cache (rounded inputs, 5-min TTL; the delay in a sibling key); a `TomTomDailyCounter` guards the 2,500/day free tier and reports on `/admin/model-usage`. Note from the first live calls: a future `departAt` answers from historical speed profiles (the rush shows up in `travelTimeInSeconds`), while `trafficDelayInSeconds` is live congestion — meaningful mainly for departures near now.

Original decision (26 Oct):
OpenRouteService is the routing provider (`IRoutingClient` in `Tempo.Integrations.Maps`). Rationale: free, and bike/walk/car are what the demo needs; transit is out of scope for the hackathon. Section 4 implements the ORS Directions API with profiles `cycling-regular` (bike), `foot-walking` (walk) and `driving-car` (car); geocoding uses ORS's Pelias geocode endpoints under the same key. Mode `train` stays in the mode enum but is disabled by default in `UserSettings`, and `plan.options` skips disabled modes. `Routing:*` config is the ORS base URL + API key. Also recorded here: Meta Model API credits are live, so section 1 builds `MetaApiModels` first and `BasetenModels` second as the fallback (`Models:Primary = MetaApi`).

### D16 — Web Push (decided 27 Oct 2026; replaces Telegram)
Nudges and habit proposals are Web Push notifications to the installed PWA. VAPID keys live in user-secrets (`Push:VapidPublicKey` / `Push:VapidPrivateKey`), never in the repo. A `PushSubscription` entity stores `endpoint`, `p256dh`, `auth`, `userAgent`, `createdAt`; the user subscribes from Settings; the service worker handles `push` (show notification) and `notificationclick` (deep link). Payload: `{kind, title, body, url, nudgeId|habitId}`. A push endpoint answering 410/404 means the subscription is gone and the row is deleted. Rationale: one surface (the PWA) for everything, no bot dependency, and notifications work with the app closed — at the cost of requiring the iOS home-screen install and a stable origin.

### D17 — Non-health boundary (decided 27 Oct 2026)
What TEMPO does: plan departures, time nudges to the moment of action, and keep every interaction one number / one tap, designed with time blindness and executive-function load in mind. It says "built for people who find time hard" and nothing stronger.
What TEMPO will not do: ask about, store, or infer anything diagnosis-related; use symptom or condition language anywhere in UI, prompts, or notifications; or claim to treat, manage, or improve any condition. A string scan test enforces the banned terms in UI copy, prompt files and the README.

### D18 — Straight-line routing fallback (decided 27 Oct 2026)
OpenRouteService went down twice in two days. When `IRoutingClient` throws `RoutingUnavailableException` for every enabled mode, the planner falls back to a deterministic straight-line estimate: haversine distance × 1.3 road factor at 15 km/h (bike), 4.8 km/h (walk), 35 km/h (car); stops via the same method. These options carry `isEstimated: true`: the agent's context and the nudge text must say "roughly" (enforced in `notify.send`, not the prompt), the trace notes "routing unavailable, used estimate", and the Now screen shows an "estimated" label. Partial availability never estimates — real options only. The next tick after routing recovers replaces the estimate in place, or wakes the agent when the real leaveAt moves more than 5 minutes. `Routing:FallbackEnabled` (default true) switches the fallback off.

## Risks / Trade-offs
- iOS push requires the home-screen install and a stable origin; a quick-tunnel restart changes the hostname and kills the subscription — a named tunnel is mandatory before installing the PWA.
- Meta API credits may arrive late. Mitigation: Baseten path first, API path second; `IMetaModels` makes the swap a config change.
- Rate limits on the Meta API during a 2-minute tick loop. Mitigation: the tick only invokes the agent when an input changed since the last run; otherwise the committed plan stands.
- Spark Open on Baseten may differ in tool-calling reliability from Spark. Mitigation: the scripted-fake agent tests pin the expected sequences; a "tool-call sanity" integration test runs against both providers in CI (skipped without keys).
- Routing transit quality in NL varies; resolved by D15: transit is out of scope, ORS covers bike/walk/car for free.
- Foreground-only location: nudges when the app is closed depend on Web Push. Acceptable.
- Tunnel is a single point of failure for the live demo. Mitigation: demo mode (GPX replay) and the video are the primary artefacts; live is a bonus.
- Vision extraction from photos is fuzzy. Mitigation: user always confirms; the confirm card is the feature, not the extraction accuracy.

## Migration Plan
Greenfield. EF Core migrations from day one; `dotnet ef database update` in compose startup.

## Open Questions
None. The one blocking decision (routing provider) is resolved in D15.
