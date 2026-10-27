# TEMPO

A proactive time-keeper agent. TEMPO watches your calendar and location, computes real departure times, nudges you to leave on time, and learns the small recurring stops (coffee, groceries) that make you late — so they're part of the plan, not the reason you missed it. You can talk to it ("I'm grabbing a coffee first") or show it things (a photo of an invite) and the plan adjusts.

## Who it's for

TEMPO is built for people who find time hard. Time blindness — not feeling how far away "in 40 minutes" is, or how long "a quick stop" takes — is daily reality for many people with ADHD and other neurodivergent people, and for plenty of neurotypical people too. TEMPO is designed with that audience in mind: one number per screen, one tap per action, one calm recovery when you're late, and nudges at the moment of action instead of a schedule to track. TEMPO is a productivity tool, not a health product: it never asks about, stores, or claims anything about a diagnosis or condition.

## Built during the hackathon

**Meta Global AI Developer Hackathon — Indie Developer track, "Build for Your World".** Everything in this repository was built during the building period, starting from an empty repo (first commit 26 Oct 2026, spec only; first code 26 Oct 2026 20:13). No code was copied from any prior project.

**Tooling:** Built with Claude Code; section 11 (capture) with Muse Code.

## Meta tools

| Model | Where | Why | Provider |
|---|---|---|---|
| Muse Spark (`muse-spark-1.3`) | The agent loop: reads state through tools, picks one departure option, words every nudge | Real agentic tool-calling with judgement, not a text generator behind a form | Meta Model API |
| Muse Spark (vision) | Photo → appointment extraction (capture) | Reads WhatsApp screenshots, letters and posters into literal text fields | Meta Model API |
| Muse Spark (structured output) | Intent classification for voice, habit-proposal wording | Strict JSON schemas keep the closed intent set closed | Meta Model API |
| Muse Voice Transcribe (`muse-voice-transcribe-1.0`) | Hold-to-talk speech-to-text (Dutch and English) | One press turns an utterance into a plan change | Meta Model API |
| Muse Spark Open (`meta-models/muse-spark-open-1026`) | Same agent role as Spark — automatic fallback on 429/5xx, or primary via `Models:Primary=Baseten` | Open-weight path; protects the demo from rate limits | Baseten |
| Muse Code | Built section 11 (capture appointment) end to end | Part of the submission's tooling story | — |

## Architecture in 10 lines

1. **Tools compute, the model decides.** All time arithmetic, routing and clustering is deterministic C#; Muse Spark chooses between options the planner computed and words the message.
2. Guards live in code, not prompts: `plan.commit` only accepts an option id from the same run's `plan.options`; `notify.send` validates every time in the text against the plan and refuses early, duplicate or quiet-hours nudges.
3. Google Calendar is read-only; captured appointments are local-only. The agent can never write to a calendar.
4. Raw GPS lives in Redis for max 24 h; only collapsed `Visit`s persist. Habits require ≥3 visits in the same context and your explicit yes.
5. A 2-minute planner tick wakes the agent only when inputs changed, the nudge window opened, or lateness began — plus a hard daily run cap.
6. If a run ends without a valid nudge while one is owed, a deterministic template built from the plan is sent instead; if the model is down, a deterministic fallback commits the best option.
7. Every agent run is recorded as a trace; every nudge carries "Why?" that renders the tool calls as plain language.
8. Nudges and habit proposals are Web Push (VAPID) to the installed PWA; one nudge per plan version, a >5-minute plan shift presents itself as an update.
9. The PWA (React + Vite + Tailwind) is the only UI: Now, Today, Habits, Settings, served by the API from `wwwroot` behind a stable-origin tunnel.
10. One `IMetaModels` abstraction, two providers (Meta API, Baseten), per-call accounting on `/admin/model-usage`.

## Things we learned about the API

- **Strict JSON matters.** With `strict:false`, structured output occasionally emitted a corrupted property key (observed live: `"placeName promenade"` instead of `"placeName"`). Schemas that declare `additionalProperties:false` + all-props-required now go `strict:true` on the wire, which eliminated it completely (design.md D2).
- **ASR is WAV-only and language-silent.** `POST /v1/asr/transcribe` accepts multipart WAV (mono integer PCM, 16/24 kHz) — not webm/opus — and returns no detected-language field. The PWA hand-rolls a WAV encoder (downsample → 16-bit PCM → RIFF header) and the intent classifier is language-agnostic (Dutch and English both work).
- **Tool calling needs the echo.** The assistant message including its `tool_calls` must be sent back before the `role:"tool"` results, or the API answers 400; `tool_choice` supports only `"auto"`.

## How to run

```sh
docker compose up -d                                    # Postgres 16 + pgvector (:5433), Redis (:6380)
(cd Tempo.Web && npm install && npm run build)          # PWA → Tempo.Api/wwwroot
cp .env.example .env                                    # then fill in your keys (see below)
dotnet user-secrets set "Models:MetaApi:ApiKey" "…" --project Tempo.Api
dotnet run --project Tempo.Api                          # http://localhost:5181, migrates on startup
```

Secrets live in user-secrets or a gitignored `Tempo.Api/appsettings.local.json` — see [.env.example](.env.example) for every key. For the phone: a stable HTTPS origin (named Cloudflare tunnel) is required for the PWA install and push subscriptions; see [architecture.md](architecture.md).

## Model usage

Honest numbers from `/admin/model-usage` land here before submission. Snapshot while building (27 Oct, one API process): 50 Muse Spark calls, 0 failures, ~105k prompt + ~34k completion tokens; routing cache 42 hits / 36 misses.

- Spec: the full OpenSpec proposal, design decisions D1–D17 and per-capability specs are in [`openspec/`](openspec/).
- Demo: 3-minute video (lands at submission), demo mode replays a GPX trail from home — leave nudge to late recovery inside five minutes.
