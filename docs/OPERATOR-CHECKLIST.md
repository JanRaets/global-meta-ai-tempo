# Operator checklist — human steps before TEMPO is demo-ready

In dependency order. Each item: why it waits on you, exact steps, how to verify, what it unblocks.
API runs on `http://localhost:5181`; `/admin/*` endpoints are dev-only (404 otherwise).

## 1. Named tunnel: tempo.personalbot.dev — ✅ DONE 27 Oct (unblocked 2, 3, 6)

Live via the `tempo` Cloudflare tunnel (zone personalbot.dev), run by the LaunchAgent
`~/Library/LaunchAgents/dev.personalbot.tempo-tunnel.plist` (KeepAlive; starts at login —
after a reboot it is up once you log in). Config: `~/.cloudflared/config.yml` → `http://localhost:5181`.

Verify any time: `https://tempo.personalbot.dev/health` → `{"status":"ok"}`.
Logs: `~/Library/Logs/tempo-tunnel.log`. Restart: `launchctl kickstart -k gui/$(id -u)/dev.personalbot.tempo-tunnel`.

## 2. Owntracks: rotated key + new hostname — REQUIRED (live habit data; tasks 5.7)

Why you: the settings live in the app on your phone. The old key was rotated on 27 Oct and
no longer works; location posting is dead until this is done.

1. New key: `curl http://localhost:5181/admin/location-key` (shows key + full URL).
2. Owntracks → Settings: Mode **HTTP**; URL `https://tempo.personalbot.dev/location/owntracks?key=<key>`;
   Tracking: Significant changes (monitoring 1) or Move during demo walks.

Verify: `curl http://localhost:5181/admin/location-stats` → `pointsLastHour` > 0 after moving.

## 3. PWA install + notifications + permissions — REQUIRED, needs item 1

Why you: physical phone, Safari prompts, Apple's install gesture. Do this only AFTER item 1 —
installing against a quick tunnel binds everything to a hostname that will die (task 0.7).

1. iPhone Safari → `https://tempo.personalbot.dev` → Share → **Add to Home Screen** → open from
   the home screen icon (must run standalone for push on iOS).
2. Grant location when asked (header dot turns blue "live").
3. Settings tab → Notifications → **Enable** → allow → **Send test** → a notification arrives.
4. Hold-to-talk once to trigger the microphone permission.

Verify: test notification on the lock screen; `GET /api/settings` shows `pushSubscriptions: 1`;
leave/late nudges from then on arrive as push. Unblocks item 6.

## 4. Baseten key → task 13.b — REQUIRED for the open-weight bonus; independent of 1–3

Why you: the key only exists in your Baseten account. Nothing in the repo has it (checked:
user-secrets, appsettings.local.json, env — task 13.b is blocked exactly here).

1. `cd Tempo.Api && dotnet user-secrets set "Models:Baseten:ApiKey" "<key>"`
2. Tell Claude "run 13.b": Baseten smoke un-skips, then the five agent scenarios run with
   `Models__Primary=Baseten` and the traces land in the section log.

Verify: `dotnet test --filter Baseten_tool_call_round_trip` reports **Passed** (not Skipped);
`/admin/model-usage` `byProvider` gains a `Baseten` row.

## 5. Google OAuth + real calendar — OPTIONAL for the demo (captured/seeded appointments work
without it); REQUIRED for the "real calendar" story. Independent of items 1–3.

Why you: Google Cloud Console account and consent screen are yours. No startup failure without
it — calendar sync just logs that it is not connected.

1. console.cloud.google.com → project → OAuth consent (External, test user = your account) →
   Credentials → OAuth client ID (Web): redirect URI exactly `http://localhost:5181/auth/google/callback`.
2. `cd Tempo.Api && dotnet user-secrets set "Google:ClientId" "<id>"` and
   `dotnet user-secrets set "Google:ClientSecret" "<secret>"`; restart the API.
3. On the Mac: open `http://localhost:5181/auth/google/connect`, approve.
4. Put real appointments (with locations!) in the calendar for the demo days.

Verify: `curl -X POST http://localhost:5181/sync/calendar` then `GET /api/timeline/today`
shows them. Note: sync runs every 5 min once connected.

## 6. First real-phone voice + capture checks — REQUIRED, needs item 3

Why you: a real microphone, a real camera, your voice. The pipelines are live-verified with
synthesized WAVs and test images; the phone hardware path is the last unknown.

1. Now screen → hold to talk → "skip the coffee today" → result card shows transcript + change → Undo.
2. Try one Dutch utterance ("ik ga eerst langs de Albert Heijn").
3. Camera button → photo a WhatsApp invite → confirm card → Add → it appears in Today
   tagged "captured".

Verify: cards show sensible transcripts; `GET /api/timeline/today` has the captured item.

## 7. Rotate the Meta API key — RECOMMENDED before submission; independent

Why you: the key is yours at the Meta dev dashboard; the current one existed in a tracked file
briefly on day 1 (never committed — gitleaks is clean) and has been pasted around.

1. dev.meta.ai → create a new API key, revoke the old one.
2. The key lives in TWO places and `appsettings.local.json` WINS (it loads after user-secrets):
   update `Tempo.Api/appsettings.local.json` → `Models:MetaApi:ApiKey`, and
   `cd Tempo.Api && dotnet user-secrets set "Models:MetaApi:ApiKey" "<new>"` (tests read this).
3. Restart the API.

Verify: `/health` ok and one agent run succeeds (`/admin/agent-run` or wait for a tick);
`dotnet test --filter MetaApi_tool_call_round_trip` passes.

## 8. D17 vs D18 — RESOLVED, no action

Decision recorded: the non-health boundary stays **D17**. The repo has no "D18" reference
(checked), tasks.md needs no change, and the numbering is final.

---

After these: section 14 (shot list, record, edit, upload, submit before 4 Nov 21:00 ET) —
tracked in tasks.md, not here. Items 2, 3 and 6 are the ones that need daylight and movement;
everything else works from the desk.
