# Project Context

## Purpose
TEMPO is a proactive time-keeper agent: it watches a user's calendar and location, computes real departure times, nudges them to leave on time, and learns habitual stops (coffee, groceries) so those are factored into timing and routing. The user can also talk to it ("I'm grabbing a coffee first") or show it things (a photo of an invite) and the agent adjusts the plan.

Entry for the Meta Global AI Developer Hackathon, Indie Developer track, problem statement "Build for Your World". Building period 27 Oct – 4 Nov 2026; submission (public repo + 3-minute demo video) due 4 Nov 21:00 ET. Single-user MVP. Fresh build: the repo is created empty on 27 Oct 2026 and no code is copied from any prior project.

## Tech Stack
- Backend: .NET 8, FastEndpoints, PostgreSQL 16 + pgvector, Redis, Hangfire
- Frontend: React 18, TypeScript, Vite, Tailwind — mobile-first PWA (installable, browser Geolocation API and MediaRecorder for voice); Owntracks as the all-day background location source during the building period
- Models (Meta only, behind `IMetaModels`):
  - Muse Spark via Meta Model API — the agent model: tool-calling loop, judgement, wording, vision (photo → appointment)
  - Muse Spark Open via Baseten (`meta-models/muse-spark-open-1026`) — fallback for the same role when Meta API credits are unavailable; also the open-weight bonus eligibility path
  - Muse Voice Transcribe via Meta Model API — speech to text for voice input
- Routing: Google Routes API or OpenRouteService (see decisions)
- Notifications: Telegram Bot API for push (inline keyboards for approvals); the PWA mirrors every nudge and action in-app
- Infra: docker-compose for local; one public HTTPS endpoint (tunnel or small VM) so the phone can reach the API
- Dev tooling: Muse Code for at least the scaffold and one full capability (recorded in README as part of the submission); Claude Code for the rest

## Project Conventions

### Code Style
- C#: nullable enabled, file-scoped namespaces, records for domain values, `sealed` by default
- TS: strict, functional components, no default exports
- Deterministic code does all time arithmetic and all routing calls. The model never produces a time, a duration or a coordinate; it chooses between options the tools give it and explains them.

### Architecture Patterns
- Vertical slices per capability; FastEndpoints request/response per endpoint
- The agent is a Muse Spark tool-calling loop over a typed tool surface (`calendar.*`, `location.*`, `routing.*`, `plan.*`, `habits.*`, `notify.*`); every tool is deterministic and unit-tested
- Hard approval model enforced in the type system: only `ConfirmedHabit` reaches the planner, and it is constructible only from the confirmation handler
- Every agent run is recorded as a trace (tool calls, arguments, results, final decision) so the decision is explainable and the demo can show it
- Patterns above are implemented from scratch in this repo; no external project code is imported
- Background work in Hangfire jobs; no logic in controllers

### Testing Strategy
- xUnit; planner, clustering and every tool are pure and unit-tested
- Agent loop tested with a scripted fake `IMetaModels` that replays tool-call sequences
- Integration tests with Testcontainers for Postgres/Redis
- One E2E demo script that seeds data and walks the full nudge → habit → voice → recovery path

### Git Workflow
- Trunk-based; one change per OpenSpec proposal; conventional commits; public repo from the first commit

## Domain Context
- Appointment: calendar event with a resolvable location
- Place: geocoded point with a name/category (home, café, supermarket, …)
- Visit: time-bounded stay at a Place, derived from raw location points
- Habit: recurring Visit pattern in a context (weekday, time bucket, travel direction, adjacent appointment type)
- DeparturePlan: leave-at time + mode + included stops for one appointment
- Nudge: outbound message tied to a plan version
- Intent: a user utterance (voice or text) turned into a structured plan adjustment
- AgentTrace: the recorded tool-call sequence and decision of one agent run

## Important Constraints
- Meta models for every model call; no other model provider SDK in the repo
- The model never computes a time; tools do
- Raw GPS points live in Redis for max 24h; only Visits are persisted
- Agent never creates, moves or deletes calendar events
- Quiet hours respected for all nudges
- Everything demoed must be built during the hackathon; the README states what was built and with which Meta tools

## External Dependencies
- Meta Model API (Muse Spark, Muse Voice Transcribe)
- Baseten (Muse Spark Open)
- Google Calendar API (read-only scope)
- Routing provider API
- Telegram Bot API
