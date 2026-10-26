# Tasks: add-tempo-timekeeper

Building period 27 Oct – 4 Nov 2026. Target day per section is a guide, not a gate. Sections marked **[Muse Code]** are built with Muse Code and recorded as such in the README.

## 0. Decisions & scaffold — Tue 27 Oct
- [x] 0.1 Record human decisions (routing provider; credit status → provider build order) in `design.md`
- [x] 0.2 `git init` empty public repo; first commit is the OpenSpec folder and a README stub stating hackathon, track, "everything here is built 27 Oct – 4 Nov"
- [x] 0.3 Create `Tempo.sln`: `Tempo.Core`, `Tempo.Agent`, `Tempo.Api`, `Tempo.Jobs`, `Tempo.Integrations.Meta`, `Tempo.Integrations.Google`, `Tempo.Integrations.Maps`, `Tempo.Integrations.Telegram`, `Tempo.Tests`
- [x] 0.4 Create `Tempo.Web` (Vite + React 18 + TS + Tailwind + vite-plugin-pwa), served as static files by `Tempo.Api`
- [x] 0.5 docker-compose: Postgres 16 + pgvector, Redis; EF Core migrations run on startup
- [x] 0.6 Config schema: `Models:*`, `Routing:*`, `Telegram:*`, `Google:*`; fail fast on missing values
- [ ] 0.7 Public HTTPS endpoint (tunnel); verify the phone can open the PWA and grant geolocation + microphone — *tunnel documented in architecture.md; phone verification is a physical step for the builder*
- [x] 0.8 Commit `architecture.md`; README tooling line (*decision: "Built with Claude Code; section 11 (capture) with Muse Code."*)

## 1. Meta models — Tue 27 / Wed 28 Oct
- [x] 1.1 Define `IMetaModels` (`ChatAsync` with tools, `ChatJsonAsync<T>`, `DescribeImageAsync<T>`, `TranscribeAsync`) and `ToolCall`/`ToolDefinition` types
- [x] 1.2 Implement whichever provider has working credits first (Baseten `meta-models/muse-spark-open-1026` or Meta API Muse Spark); then the other
- [x] 1.3 Muse Voice Transcribe in `MetaApiModels`; `BasetenModels.TranscribeAsync` delegates to it
- [x] 1.4 Fallback wrapper: primary → fallback on 429/5xx; provider recorded on every result
- [x] 1.5 Call accounting: log provider, model, purpose, tokens, latency; `GET /admin/model-usage`
- [x] 1.6 Scripted fake `IMetaModels` for tests
- [x] 1.7 Tests: provider selection, JSON retry, double failure throws, fallback on 429, no foreign SDK packages (CI grep)
- [x] 1.8 Integration smoke (skipped without keys): one tool-call round trip against each real provider — *MetaApi verified live 26 Oct (200 OK, muse-spark-1.3 called the tool); Baseten reports Skipped until a `Models:Baseten:ApiKey` exists. Key read from user-secrets / appsettings.local.json / env; missing key → Skipped, never Passed*

## 2. Domain (Tempo.Core) — Wed 28 Oct
- [x] 2.1 Entities: `Place`, `Appointment` (with `Source = Synced | Captured`), `Visit`, `Habit`, `DeparturePlan`, `PlanOption`, `Nudge`, `Intent`, `AgentTrace`, `UserSettings` — *`PlanOption` is a computed record, defined with the planner tool in section 6 (design D6: options are ephemeral, only the committed plan persists)*
- [x] 2.2 `ConfirmedHabit` with `internal` constructor; `ConfirmHabitHandler` as sole producer
- [x] 2.3 `ContextKey` value type and `Matches` logic
- [x] 2.4 EF Core `TempoDbContext` + initial migration
- [x] 2.5 Tests: `ConfirmedHabit` has no public constructor; `ContextKey` matching incl. weekend mismatch

## 3. Calendar sync — Wed 28 Oct
- [x] 3.1 Google OAuth (readonly scope), encrypted token storage — *flow built and unit-tested (scope, state, Data Protection at rest); the live consent click-through needs `Google:ClientId/Secret` in appsettings.local.json and a browser — builder step*
- [x] 3.2 `CalendarSyncJob` every 5 min + on-demand; window [−1h, +7d]
- [x] 3.3 Geocode location → `Place` with 50 m reuse; handle unresolvable and virtual
- [x] 3.4 Raise `AppointmentChanged`; cancel pending nudges on delete
- [x] 3.5 Tests: create/move/delete against a fake Calendar client

## 4. Routing — Wed 28 / Thu 29 Oct
- [x] 4.1 `IRoutingClient.Estimate(from, to, mode, departAt)` and `Detour(from, via[], to, mode)`
- [x] 4.2 Implement chosen provider; 5-min Redis cache keyed by rounded inputs — *ORS Directions; 429/5xx/network → `RoutingUnavailableException`; verified live: Zeist→Utrecht Centraal bike 37.3 min, detour via De Bilt 40.6 min*
- [x] 4.3 Tests: fixed route pair stable; cache hit

## 5. Location ingest — Thu 29 Oct
- [x] 5.1 `POST /location` with per-user API key; validation; Redis list 24h TTL
- [x] 5.2 `ILocationSource` + `GpxReplaySource` with speed factor — *source + tests done; wired to the demo-mode toggle in section 12/13*
- [x] 5.3 `CollapseVisitsJob` every 10 min: stay detection (≥5 min, 75 m), reverse-geocode, create/update `Visit`
- [x] 5.4 `location.current()` with `isStale` after 30 min
- [x] 5.5 PWA: `watchPosition` → `POST /location`, throttled; permission prompt with one-line explanation — *minimal hook + live dot; real Now screen in section 12*
- [x] 5.6 `POST /location/owntracks` adapter (HTTP mode JSON → standard point; returns `[]`) — *verified live through the tunnel; key via ?key= or Authorization header*
- [ ] 5.7 **Install Owntracks on the builder's phone, point it at the tunnel, and leave it running for the rest of the building period** so real Visits accumulate — *builder step; exact settings reported 26 Oct, server + tunnel left running*
- [x] 5.8 Tests: stay detected, transit ignored, ongoing stay updated, stale fallback, Owntracks mapping

## 6. Departure planning tools — Thu 29 / Fri 30 Oct
- [x] 6.1 `plan.options(appointmentId)` per design D6: modes × stop subsets, per-plan input changes (skipped stop, extra stop, late offset)
- [x] 6.2 `plan.commit(optionId, reason)` with same-run optionId validation; `plan.current`
- [x] 6.3 Plan versioning with ±3 min routing tolerance
- [x] 6.4 Deterministic fallback commit when no agent run commits within 2 min — *`FallbackCommitAsync` done and tested; the 2-minute tick that invokes it is wired with the section 7 triggers*
- [x] 6.5 Tests: option enumeration, commit valid/invalid optionId, proposed habit ignored, jitter, fallback commit

## 7. Agent loop — Fri 30 / Sat 31 Oct
- [x] 7.1 Tool registry with JSON schemas: `calendar.upcoming`, `location.current`, `plan.options`, `plan.commit`, `plan.current`, `habits.confirmed`, `notify.send`, `trace.note`
- [x] 7.2 System prompt (rules per design D7) + loop with 8-call limit; `AgentTrace` persisted per run — *prompt in Tempo.Agent/Prompts/agent.system.md, loaded at startup*
- [x] 7.3 Triggers: planner tick (only when inputs changed), `AppointmentChanged`, `VisitCreated`, habit confirmed, settings changed, intent applied, appointment captured — *tick (2 min, inputs hash + window/late checks) + domain events wired; settings/intent/capture call the same TriggerAsync path when their sections land; failed run → FallbackCommitAsync*
- [x] 7.4 `notify.send` guards: duplicate, quiet hours (+ late exception), text validation (leave time, mode, ≤2 sentences, no unknown times)
- [x] 7.5 Late-state invocation: exactly one commit + one `Late` nudge
- [x] 7.6 `GET /traces/{nudgeId}` rendered as plain-language steps
- [x] 7.7 Tests with scripted fake: normal plan, nudge window, duplicate refused, fabricated optionId refused, call limit, late skip-stop, nothing feasible, unchanged inputs → no model call
- [x] 7.8 Run against the real provider on a real appointment; fix prompt until 5/5 runs commit sensibly — *5/5 live Muse Spark runs 26 Oct: normal, window+nudge, duplicate handled, only-feasible-mode, late recovery; no fabricated times, no refusal loops; traces reported*

## 8. Habit learning — Sat 31 Oct
- [ ] 8.1 `HabitInferenceJob` nightly + on-demand: thresholds ≥3 visits / stddev ≤8 min
- [ ] 8.2 Dedup; 30-day rejected suppression
- [ ] 8.3 Spark proposal wording via `ChatJsonAsync<HabitProposalText>`; numeric facts immutable
- [ ] 8.4 Telegram batch (one message/day) with Confirm/Reject + Why?; callbacks → `ConfirmHabitHandler` / reject
- [ ] 8.5 Endpoints: `GET /habits?state=`, `POST /habits/{id}/confirm|reject`
- [ ] 8.6 Tests: detected, insufficient, irregular, already-decided, confirm triggers agent run

## 9. Nudging transport — Sat 31 Oct
- [ ] 9.1 Telegram send with 3× backoff; `Nudge.status`; "Why?" inline button → trace text
- [ ] 9.2 Replacement nudge on >5 min plan shift
- [ ] 9.3 Template fallback when the run ends without a valid send
- [ ] 9.4 Tests: sent, replaced, template fallback, quiet-hours deferral of habit batch

## 10. Voice input — Sun 1 Nov
- [ ] 10.1 PWA hold-to-talk (Web Audio → WAV mono PCM 16 kHz, ≤15 s; ASR API is WAV-only per design D2) → `POST /voice`; text-field fallback when mic denied
- [ ] 10.2 Server: `TranscribeAsync` → `ChatJsonAsync<Intent>` (closed set incl. `Unknown`)
- [ ] 10.3 Apply intent as per-plan input change; trigger agent run; `ConfirmHabit` routes through `ConfirmHabitHandler`
- [ ] 10.4 Result card: transcript + one-line change + Undo (10 s)
- [ ] 10.5 Tests: each intent from a fixed transcript via scripted fake; Unknown → three chips; undo reverts

## 11. Capture appointment — Sun 1 Nov **[Muse Code]**
- [ ] 11.1 PWA camera button → `POST /capture` (resized to ≤1600 px client-side)
- [ ] 11.2 Server: `DescribeImageAsync<CapturedAppointment>`; relative-date resolution in user timezone; geocode via calendar path
- [ ] 11.3 Confirm card with editable time; Add → `Appointment { Source = Captured }`; never written to Google
- [ ] 11.4 Tests: relative date, no appointment found, captured appointment gets a plan and a nudge

## 12. Mobile PWA — Mon 2 Nov
- [ ] 12.1 API: `GET /now`, `GET /timeline/today`, `GET /nudges?limit=20`, `GET/PUT /settings`, `GET /habits?state=`
- [ ] 12.2 PWA shell: manifest, service worker, standalone, bottom tabs, safe-area insets
- [ ] 12.3 **Now**: dominant countdown, reason line, mode icon, hold-to-talk + camera buttons, recent nudges with Why?; late state = full-screen colour + single recovery button
- [ ] 12.4 **Today**: timeline incl. captured appointments; greyed no-place/virtual
- [ ] 12.5 **Habits**: proposed cards + confirmed list with pause
- [ ] 12.6 **Settings**: home, modes, pinned mode, nudge lead, quiet hours, demo-mode toggle
- [ ] 12.7 **Trace sheet**: plain-language steps + provider footer
- [ ] 12.8 Polling 15 s; optimistic UI on confirm/reject/undo
- [ ] 12.9 Match the design canvas (TEMPO mobile screens) for type, colour and spacing

## 13. Hardening & demo prep — Tue 3 Nov
- [ ] 13.1 Seed script as safety net (home, 3 places, a week of Visits → two habit candidates, today's appointments) — used only if real data is thin
- [ ] 13.2 Demo GPX trail: leave nudge → late nudge within a 5-minute replay
- [ ] 13.3 E2E: seed → inference → proposal → confirm → plan includes stop → voice "skip coffee" → plan drops stop → capture photo → new appointment → replay lateness → recovery nudge → trace available
- [ ] 13.4 Manual nudge review: 10 real runs, ≤2 sentences, mode + leave time, no invented times
- [ ] 13.5 README: what was built (all), Meta tools table (which model, where, why), Muse Code sections, how to run, model-usage numbers from `/admin/model-usage`
- [ ] 13.6 `.env.example`; confirm repo is public and clean of keys

## 14. Video & submission — Tue 3 / Wed 4 Nov
- [ ] 14.1 Shot list from design.md demo loop; 3:00 max; ends on the trace sheet
- [ ] 14.2 Record: phone screen recording + second phone for the walk; voice-over
- [ ] 14.3 Edit; upload; verify link is public
- [ ] 14.4 Submit on the CV platform before 4 Nov 21:00 ET (03:00 5 Nov Amsterdam); post on X for the viral bonus
