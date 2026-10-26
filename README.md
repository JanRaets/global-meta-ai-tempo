# TEMPO

A proactive time-keeper agent. TEMPO watches your calendar and location, computes real departure times, nudges you to leave on time, and learns the small recurring stops (coffee, groceries) that make you late — so they're part of the plan, not the reason you missed it. You can talk to it ("I'm grabbing a coffee first") or show it things (a photo of an invite) and the plan adjusts.

**Meta Global AI Developer Hackathon — Indie Developer track, "Build for Your World".**

Everything in this repository is built during the building period, **27 Oct – 4 Nov 2026**, starting from this empty repo. No code is copied from any prior project.

- Models: Meta only — Muse Spark (agent loop, vision, wording), Muse Spark Open on Baseten (fallback / open-weight path), Muse Voice Transcribe (speech) — behind a single `IMetaModels` abstraction.
- Principle: **tools compute, the model decides.** No model output is ever used as a time, duration or coordinate; guards live in code.
- Spec: the full OpenSpec proposal, design and per-capability specs are in [`openspec/`](openspec/).

## Built with Muse Code

Section 5 (location ingest) is planned to be built with Muse Code; which sections were built with which Meta dev tool is recorded here honestly at submission time.

*(Full README — Meta tools table, architecture, how to run, model-usage numbers — lands in section 13 of [tasks.md](openspec/changes/add-tempo-timekeeper/tasks.md).)*
