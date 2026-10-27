# TEMPO

A proactive time-keeper agent. TEMPO watches your calendar and location, computes real departure times, nudges you to leave on time, and learns the small recurring stops (coffee, groceries) that make you late — so they're part of the plan, not the reason you missed it. You can talk to it ("I'm grabbing a coffee first") or show it things (a photo of an invite) and the plan adjusts.

## Who it's for

TEMPO is built for people who find time hard. Time blindness — not feeling how far away "in 40 minutes" is, or how long "a quick stop" takes — is daily reality for many people with ADHD and other neurodivergent people, and for plenty of neurotypical people too. TEMPO is designed with that audience in mind: one number per screen, one tap per action, one calm recovery when you're late, and nudges at the moment of action instead of a schedule to track. TEMPO is a productivity tool, not a health product: it never asks about, stores, or claims anything about a diagnosis or condition.

**Meta Global AI Developer Hackathon — Indie Developer track, "Build for Your World".**

Everything in this repository is built during the building period, **27 Oct – 4 Nov 2026**, starting from this empty repo. No code is copied from any prior project.

- Models: Meta only — Muse Spark (agent loop, vision, wording), Muse Spark Open on Baseten (fallback / open-weight path), Muse Voice Transcribe (speech) — behind a single `IMetaModels` abstraction.
- Principle: **tools compute, the model decides.** No model output is ever used as a time, duration or coordinate; guards live in code.
- Spec: the full OpenSpec proposal, design and per-capability specs are in [`openspec/`](openspec/).

## Tooling

Built with Claude Code; section 11 (capture) with Muse Code.

*(Full README — Meta tools table, architecture, how to run, model-usage numbers — lands in section 13 of [tasks.md](openspec/changes/add-tempo-timekeeper/tasks.md).)*
