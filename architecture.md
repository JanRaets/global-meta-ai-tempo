# TEMPO architecture

The spec is the contract: see [`openspec/`](openspec/) (proposal, design decisions D1–D15, per-capability specs). This file is the 5-minute orientation.

## Principle

**Tools compute, the model decides.** All time arithmetic, routing and clustering is deterministic C# exposed to Muse Spark as tools. The model reads state through tools, picks between options the planner computed, and words the message. It can never emit a time, duration or coordinate as a fact — guards live in the tools (`plan.commit` only accepts an `optionId` from the same run's `plan.options`; `notify.send` validates the text against the committed plan), not in the prompt.

## Solution layout

| Project | Role |
|---|---|
| `Tempo.Core` | Domain entities, `ContextKey`, `ConfirmedHabit` (internal ctor), `IMetaModels`, config options, `TempoDbContext` |
| `Tempo.Agent` | Muse Spark tool-calling loop, tool registry, system prompt, `AgentTrace` recording |
| `Tempo.Api` | FastEndpoints HTTP API; serves the built PWA from `wwwroot`; migrates the DB on startup |
| `Tempo.Jobs` | Hangfire jobs: calendar sync, visit collapsing, habit inference, planner tick |
| `Tempo.Integrations.Meta` | `MetaApiModels` (Muse Spark + Muse Voice Transcribe), `BasetenModels` (Muse Spark Open), fallback wrapper |
| `Tempo.Integrations.Google` | Google Calendar read-only sync (no write methods exist) |
| `Tempo.Integrations.Maps` | OpenRouteService Directions + Pelias geocoding (design D15), 5-min Redis cache |
| `Tempo.Integrations.Telegram` | Nudge delivery, inline Confirm/Reject/Why? buttons |
| `Tempo.Tests` | xUnit; scripted fake `IMetaModels`; Testcontainers integration tests |
| `Tempo.Web` | Mobile-first PWA (Vite + React 18 + TS + Tailwind); builds into `Tempo.Api/wwwroot` |

## Data flow

Calendar + location points → deterministic derivation (`Appointment`, `Visit`, habit candidates) → planner computes `PlanOption`s → agent loop picks and commits one, sends guarded nudges → every run persisted as an `AgentTrace` the app renders as plain-language steps. Raw GPS lives only in Redis (24h TTL); only `Visit`s are persisted.

## Configuration

Schema in `Tempo.Core/Configuration/TempoOptions.cs`, bound from `appsettings.json`, a gitignored `Tempo.Api/appsettings.local.json` (dev keys live there), and environment variables (`Models__MetaApi__ApiKey=…`). Startup fails fast with an error naming the missing key. Each integration enforces its own required values from the section that wires it up; from section 0/1 the primary model provider's API key is required. Secrets never live in the repo — `.env.example` lands in section 13.

## Run locally

```sh
docker compose up -d                 # Postgres 16 + pgvector on :5433, Redis on :6380
(cd Tempo.Web && npm install && npm run build)   # PWA → Tempo.Api/wwwroot
Models__MetaApi__ApiKey=… dotnet run --project Tempo.Api   # http://localhost:5181
```

Host ports are 5433/6380 to avoid colliding with native installs. EF migrations apply automatically on API startup.

## Public HTTPS endpoint (phone access)

The PWA needs HTTPS for geolocation, microphone and camera. Quick tunnel:

```sh
brew install cloudflared
cloudflared tunnel --url http://localhost:5181
```

Open the printed `https://….trycloudflare.com` URL on the phone, add to home screen, grant location + microphone.

**The quick tunnel's hostname changes on every restart** — after a restart, Owntracks (and the home-screen PWA) point at a dead host until you update them. For a stable hostname, make a named tunnel once (needs a free Cloudflare account and a domain in it):

```sh
cloudflared tunnel login
cloudflared tunnel create tempo
cloudflared tunnel route dns tempo tempo.<your-domain>
cloudflared tunnel run --url http://localhost:5181 tempo
```

Owntracks setup: Settings → Mode **HTTP**, URL `https://<tunnel-host>/location/owntracks?key=<key>` (key from `GET /admin/location-key`, dev only). Check `GET /admin/location-stats` to see points arriving.
