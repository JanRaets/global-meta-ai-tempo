# Tasks: add-tempo-timekeeper

Building period 27 Oct – 4 Nov 2026. Target day per section is a guide, not a gate. Sections marked **[Muse Code]** are built with Muse Code and recorded as such in the README.

## 0. Decisions & scaffold — Tue 27 Oct
- [x] 0.1 Record human decisions (routing provider; credit status → provider build order) in `design.md`
- [x] 0.2 `git init` empty public repo; first commit is the OpenSpec folder and a README stub stating hackathon, track, "everything here is built 27 Oct – 4 Nov"
- [x] 0.3 Create `Tempo.sln`: `Tempo.Core`, `Tempo.Agent`, `Tempo.Api`, `Tempo.Jobs`, `Tempo.Integrations.Meta`, `Tempo.Integrations.Google`, `Tempo.Integrations.Maps`, `Tempo.Tests` *(an Integrations project for the old notification channel was created here and is removed in 9.1)*
- [x] 0.4 Create `Tempo.Web` (Vite + React 18 + TS + Tailwind + vite-plugin-pwa), served as static files by `Tempo.Api`
- [x] 0.5 docker-compose: Postgres 16 + pgvector, Redis; EF Core migrations run on startup
- [x] 0.6 Config schema: `Models:*`, `Routing:*`, `Push:*`, `Google:*`; fail fast on missing values *(Push:* replaces the old notification section in 9.1)*
- [x] 0.7 Public HTTPS endpoint (tunnel); verify the phone can open the PWA and grant geolocation + microphone *(named tunnel tempo.personalbot.dev live 27 Oct via LaunchAgent; phone PWA install + permissions remain operator checklist item 3)*
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
- [x] 8.1 `HabitInferenceJob` nightly + on-demand: thresholds ≥3 visits / stddev ≤8 min
- [x] 8.2 Dedup; 30-day rejected suppression
- [x] 8.3 Spark proposal wording via `ChatJsonAsync<HabitProposalText>`; numeric facts immutable — *numbers in model text validated against avg/count; template fallback on drift or model failure*
- [x] 8.4 Daily proposal batch (one notification/day) through `INudgeTransport`; confirm/reject route through `ConfirmHabitHandler` (push delivery itself lands in section 9)
- [x] 8.5 Endpoints: `GET /habits?state=`, `POST /habits/{id}/confirm|reject`
- [x] 8.6 Tests: detected, insufficient, irregular, already-decided, confirm triggers agent run
- [x] 8.7 With habit learning live: seed one confirmed morning-coffee habit and rerun the five section-7 live scenarios with stops present (keep the coffee, drop the coffee, skip-a-stop recovery); paste the traces — this is the set for the judges — *done 27 Oct: inference → live Spark wording → confirms → 5/5 scenarios incl. coffee kept/dropped, skip-stop recovery, nothing-feasible, Thursday supermarket; traces reported. Added en route: previous-bucket habit matching (D5 note) and `nudgeWindowOpen` computed in tool results*

## 9. Push notifications — Sat 31 Oct *(was Telegram; replaced 27 Oct, design D16; done 27 Oct)*
- [x] 9.1 VAPID keys (user-secrets) + `Push:*` config; delete `Tempo.Integrations.Telegram` from the solution *(new `Tempo.Integrations.Push` project; `Lib.Net.Http.WebPush` 3.3.1 — aes128gcm, required by iOS; keys generated and set in user-secrets; missing keys log a warning and fall back to the logging transport instead of failing startup, so a fresh clone still runs)*
- [x] 9.2 `PushSubscription` entity + `POST /push/subscribe` + `DELETE /push/subscribe` *(upsert by endpoint; plus `GET /push/vapid-public-key` — the browser needs it as `applicationServerKey`; migration `PushSubscriptions` also drops `UserSettings.TelegramChatId`)*
- [x] 9.3 `WebPushTransport` behind `INudgeTransport`: send to every subscription, 410/404 → delete subscription, 3× retry with backoff, `Nudge.status` recorded *(payload {kind,title,body,url,nudgeId,habitId,tag}; tag=planId so the OS replaces a stale notification; false when zero subscriptions → status Failed, honest)*
- [x] 9.4 Service worker `push` + `notificationclick` handlers; deep links to `/now` (nudges) and `/habits` (proposals) *(public/push-sw.js via workbox importScripts; live delivery to the phone pending the named tunnel — subscriptions bind to the origin; note: `/now`+`/habits` SPA screens land in section 12, which must move the JSON APIs under `/api`)*
- [x] 9.5 Android notification actions for habit Confirm/Reject → same endpoints/handler *(actions shown only when the payload names a single habit — `Nudge.HabitId`, set by the batch when exactly one proposal is due; multi-proposal batches deep-link to /habits)*
- [x] 9.6 Replacement nudge on >5 min plan shift *(detected in `NudgeService.SendAsync`: earlier Sent same-kind nudge for the appointment + >5 min leaveAt shift → `Nudge.ReplacesNudgeId`; transport titles it "TEMPO — updated plan")*
- [x] 9.7 Template fallback when the run ends without a valid send *(`NudgeTemplates` built only from the committed plan; `PlannerTickService` sends it through the same guarded notify path after any run that leaves the window/late state unnudged — including the model-down fallback commit)*
- [x] 9.8 Tests: sent, replaced, template fallback, 410 cleanup, retry, quiet-hours deferral of habit batch *(152 green; batch also tests single-proposal HabitId)*

## 10. Voice input — Sun 1 Nov *(done 27 Oct)*
- [x] 10.1 PWA hold-to-talk (Web Audio → WAV mono PCM 16 kHz, ≤15 s; ASR API is WAV-only per design D2) → `POST /voice`; text-field fallback when mic denied *(route is `POST /api/voice` — first under the /api prefix section 12 adopts; raw WAV body; `wavRecorder.ts` hand-rolls downsample + RIFF header, AudioContext created+resumed inside the pointer handler for iOS; encoder unit-tested via vitest)*
- [x] 10.2 Server: `TranscribeAsync` → `ChatJsonAsync<Intent>` (closed set incl. `Unknown`) *(`VoiceIntentService` + `Prompts/intent.system.md`; prompt receives current-plan stop names and proposed-habit names as candidates so placeId/habitId are code lookups, never model output; classifier schema is the first strict:true one — lax mode corrupted a property key live, see amended D2)*
- [x] 10.3 Apply intent as per-plan input change; trigger agent run; `ConfirmHabit` routes through `ConfirmHabitHandler` *(writes `PlanInputs` only; `IntentApplied` event → Hangfire agent run `intent:<kind>`; `/api/voice/text` is the mic-denied fallback path)*
- [x] 10.4 Result card: transcript + one-line change + Undo (10 s) *(HoldToTalk.tsx; undo info stored in Intent.ArgsJson, `POST /api/voice/undo/{id}` reverts and re-runs)*
- [x] 10.5 Tests: each intent from a fixed transcript via scripted fake; Unknown → three chips; undo reverts *(13 new server tests + 4 vitest encoder tests; 165 green; five live utterances through real ASR+Spark pasted in the section log)*

## 11. Capture appointment — Sun 1 Nov **[Muse Code]** *(done 27 Oct)*
- [x] 11.1 PWA camera button → `POST /capture` (resized to ≤1600 px client-side) *(route is `POST /api/capture` per the /api consolidation; mounted on the section-12 Now screen below hold-to-talk; canvas resize ≤1600 px + JPEG ladder to <500 KB)*
- [x] 11.2 Server: `DescribeImageAsync<CapturedAppointment>`; relative-date resolution in user timezone; geocode via calendar path *(strict schema; literal dateText/timeText, deterministic resolver EN/NL incl. day-first numerics and 19u30; place preview via IGeocoder, confirm via PlaceResolver with 50 m reuse)*
- [x] 11.3 Confirm card with editable time; Add → `Appointment { Source = Captured }`; never written to Google *(card: title, datetime-local, place/no-location, thumbnail, amber hint <0.5; Add → End=+1h + AppointmentChanged like calendar sync)*
- [x] 11.4 Tests: relative date, no appointment found, captured appointment gets a plan and a nudge *(20 resolver cases + 11 scripted service tests + 4 live images through the real Meta API; 201 green; agent planning for the captured appointment follows from the shared AppointmentChanged trigger)*

## 12. Mobile PWA — Mon 2 Nov *(done 27 Oct)*
- [x] 12.1 API: `GET /now`, `GET /timeline/today`, `GET /nudges?limit=20`, `GET/PUT /settings`, `GET /habits?state=` *(all under `/api/*`; habits + traces moved there too, so the SPA routes `/now` and `/habits` — the push deep links — no longer collide with JSON endpoints; server computes `leaveInMin`/`lateByMin`, the client only displays)*
- [x] 12.2 PWA shell: manifest, service worker, standalone, bottom tabs, safe-area insets *(tab bar on real paths via pushState/popstate so deep links land on the right screen)*
- [x] 12.3 **Now**: dominant countdown, reason line, mode icon, hold-to-talk + camera buttons, recent nudges with Why?; late state = full-screen colour + single recovery button *(camera button is a marked mount slot — section 11 lands it from Muse Code)*
- [x] 12.4 **Today**: timeline incl. captured appointments; greyed no-place/virtual *(captured tag on `Source = Captured`)*
- [x] 12.5 **Habits**: proposed cards + confirmed list with pause *(new `Habit.IsPaused` column, `POST /api/habits/{id}/pause` toggle; the planner skips paused habits — migration HabitPause, planner test)*
- [x] 12.6 **Settings**: home, modes, pinned mode, nudge lead, quiet hours, demo-mode toggle, "Enable notifications" (push subscribe + test notification; iOS install hint when not on the home screen) *(design canvas's Telegram row became this, per D16; `POST /api/push/test` sends straight through the transport)*
- [x] 12.7 **Trace sheet**: plain-language steps + provider footer *(bottom sheet from any Recent nudge's Why?)*
- [x] 12.8 Polling 15 s; optimistic UI on confirm/reject/undo *(usePoll hook: 15 s interval + refresh on tab focus; optimistic state with refresh reconciliation)*
- [x] 12.9 Match the design canvas (TEMPO mobile screens) for type, colour and spacing *(Inter Tight, #111318/#1F5EFF/#B42318 palette, 24px gutters, 128px countdown, exact tab bar/timeline/proposal-card/settings-row specs from the canvas artboards)*

## 13. Hardening & demo prep — Tue 3 Nov
*(Decided 27 Oct: the non-health boundary is design **D17** — numbering final, nothing is named D18.)* *(done 27 Oct except 13.b, blocked on the Baseten key)*
- [x] 13.a Geocoder proximity: Pelias `focus.point` = home + `boundary.country` (config, default NL); before/after on "De Jonge Haan" and "Albert Heijn"; captured appointments re-geocode on confirm when the stored place is >50 km from home *(before/after live: Albert Heijn went from South Africa to Houten NL, De Jonge Haan from Wunstorf DE to Leiderdorp NL)*
- [x] 13.b Open-weight path live: Baseten smoke un-skipped; `Models:Primary=Baseten` documented; the five 7.8 scenarios against Muse Spark Open with traces; tool-calling/strict-JSON differences reported and fixed in prompt/shape only; `/admin/model-usage` shows per-provider counts *(done 28 Oct: smoke Passed (`echo({"word":"tempo"})`); one wire difference found and fixed in shape only — Baseten 400s dotted function names (`[a-zA-Z0-9_-]` only), `BasetenModels` maps `.`↔`_` at the wire boundary, 2 tests; 5/5 scenarios live on `meta-models/muse-spark-open-1026`: normal commit, window+Leave nudge ("Grab your keys — leave at 21:26 by car…"), duplicate refused and handled gracefully, only-feasible-mode (Amersfoort, car), late recovery ("Leave now by car — you'll arrive at 21:46…"); no fabricated times, no refusal loops; usage snapshot: 29 calls, 0 failures, 61,390 prompt / 12,213 completion tokens; scenario artefacts cleaned up, nudges retagged Dev)*
- [x] 13.c Rate/credit safety: daily agent-run cap (`Agent:MaxRunsPerDay`, default 400 — raised from 200 on 29 Oct after a full live day + demo replays hit it twice) with a loud log AND a visible "paused: daily limit" line on Now (`agentPaused` on `/api/now` — the cap must never fail silently on camera); spend estimate from token counts on `/admin/model-usage`; routing cache hit/miss counters confirmed on the tick path *(live: 42 hits / 36 misses during the scenario evening)*; model-call accounting persisted to the `ModelCalls` table 29 Oct (survives restarts, `?since=` filter; no backfill)
- [x] 13.d Week-of-use resilience: push subscriptions + Hangfire jobs survive an API restart (verified live with a planted subscription); Owntracks points arriving after a 2-minute API stop are accepted (endpoint accepts replayed timestamps; the phone was stationary during the stop, so the retry itself is verified by Owntracks' documented queue behaviour); the PWA opens offline with the last-known plan and a "reconnecting" state
- [x] 13.e Live late/en-route fixes (27 Oct, from real use): PlanPhase {Upcoming, Late, EnRoute, Arrived} evaluated deterministically on tick + every location post; late/en-route plans re-estimated in place (same version, no model call; agent re-runs only when an alternative beats the choice by >5 min); no nudges once EnRoute; Now screen On-your-way/Made-it states; phase transitions as trace rows; tests per transition; GPX trail run end to end *(run live 27 Oct: Upcoming→Late 20:27:08Z, Late→EnRoute 20:33:39Z 'Moved 308 m', EnRoute→Arrived 20:36:54Z 'Within 100 m of Utrecht Centraal'; demo-off now removes demo artifacts)* *(Amended 29 Oct 2026, from the 08:20 school run: the ingest post that flips a plan to EnRoute now refreshes the ETA in the same request, later posts refresh throttled 60 s/300 m, tick stays the fallback; projected arrival past the start turns the arrival chip amber — "arriving 08:24 · 4 min late", no nudge, calm while driving.)*
- [x] 13.f Straight-line routing fallback (27 Oct, after two ORS outages): haversine ×1.3 at 15/4.8/35 km/h when every mode is unavailable, options marked isEstimated, "roughly" enforced in notify.send, trace note, Now-screen label, in-place recovery or agent wake on >5 min shift, Routing:FallbackEnabled switch — design D18, departure-planning requirement, 5 tests
- [x] 13.g Chained origin assumes home (30 Oct 2026, from real use: a 20:00 party was planned from hockey ending 17:00): look-ahead plans chain only when the gap < travel home + `Phase:StayThresholdMin` (45, replaces ChainGapHours); fresh-fix evidence names the origin (home / previous place); habits gain `Kind` (`Stop|ReturnsHome|StaysPut`) with gap-bucketed context — inferred from ≥3 comparable days, proposed/confirmed/guarded like any habit, confirmed ones override the heuristic; Today rows name the origin ("from home" / "from <title>" / "from here"); 8 tests
- [x] 13.h Push subscription self-heal (30 Oct 2026, from real use: nudges went dark — Apple answered 200 on a device-dead endpoint, and Settings refused a re-enable because a row existed): silent re-sync of this device's subscription on every app open when permission is granted, `pushsubscriptionchange` handler in the service worker (re-subscribe, register new, unregister old), Settings "Re-enable" stays reachable beside "Send test" — design D16 amendment, mobile-app push-subscription requirement + 2 scenarios
- [x] 13.1 Seed script as safety net (home, 3 places, a week of Visits → two habit candidates, today's appointments) — used only if real data is thin *(DemoModeService.SeedIfThinAsync, triggered by the demo toggle only; skips itself when real habits/visits exist)*
- [x] 13.2 Demo GPX trail: leave nudge → late nudge within a 5-minute replay *(verified live 27 Oct: toggle on → seeded appointment → "Grab your keys — leave at 20:32 by car…" → "Leave now by car and head straight there — you'll arrive at 21:03."; seed tuned to +30 min so the leave nudge precedes lateness)*
- [x] 13.3 E2E: seed → inference → proposal → confirm → plan includes stop → voice "skip coffee" → plan drops stop → capture photo → new appointment → replay lateness → recovery nudge → trace available *(EndToEndFlowTests: scripted variant always, live variant runs the whole loop against the real Meta API when a key is present — both green; CI workflow boots the API against a fresh pgvector+redis stack and smoke-tests it)*
- [x] 13.4 Manual nudge review: 10 real runs, ≤2 sentences, mode + leave time, no invented times *(ten live texts pasted in the section log; every guard held)*
- [x] 13.5 README: what was built (all), Meta tools table (which model, where, why), Muse Code sections, how to run, model-usage numbers from `/admin/model-usage` *(draft complete incl. "Who it's for" and the API findings; final usage numbers before submission)*
- [x] 13.6 `.env.example`; confirm repo is public and clean of keys *(gitleaks over all 22 commits: no leaks; repo PUBLIC; location API key rotated via new POST /admin/location-key/rotate — update Owntracks with the new key)*

## 14. Video & submission — Tue 3 / Wed 4 Nov
- [ ] 14.1 Shot list from design.md demo loop; 3:00 max; ends on the trace sheet
- [ ] 14.2 Record: phone screen recording + second phone for the walk; voice-over
- [ ] 14.3 Edit; upload; verify link is public
- [ ] 14.4 Submit on the CV platform before 4 Nov 21:00 ET (03:00 5 Nov Amsterdam); post on X for the viral bonus
