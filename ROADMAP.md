# Roadmap

This replaces the two earlier project plans (`PROJECT_PLAN.md`, Raspberry Pi; `2026_PROJECT_PLAN.md`,
Windows NUC), which are kept in [docs/archive/project-history/](docs/archive/README.md#project-history).
Both described hosts and call paths that are no longer used. Items below are taken from the code, the
current docs and the open plans in `docs/plans/`; PR numbers refer to this repository.

## Done

### Calls

- Google Voice as the call path (`GVApi` mode): SIP over WebSocket with DTLS-SRTP audio, bridged to the
  HT801 as G.711 RTP.
- Incoming calls ring the rotary phone and are answered on handset lift, with a deferred 200 OK so a
  caller hanging up during the ring stops the bell (PR #95).
- Outgoing calls dialed on the rotary phone and placed through Google Voice.
- Decline endpoint for Radio Console, `POST /api/phone/decline` (PR #94), sending `603 Decline` on the
  Google Voice leg (PR #96).
- HT801 address learned from its SIP REGISTER (PRs #67, #68).
- Bell-failure tracking and reporting to Radio Console, with acknowledgement.
- Call history persisted to SQLite.
- SIP WebSocket keep-alive and reconnect, and honest status reporting.

### Messaging for Radio Console

- Voicemail list and cached audio, SMS threads and polling push over SignalR (PRs #54, #56, #57).
- SMS send (PR #60) and Google Voice mark-read (PR #64), both shipped disabled behind
  `EnableSmsSend` / `EnableMarkRead`.
- Optional shared-key auth between the services, `X-RotaryPhone-Auth` (PR #61).

### Google Voice session

- Cookies read from the bridge Chrome over CDP; reactive refresh on 401 and honest auth-blackout status
  (PR #72, PR #78).
- Session alarm that reports a lost session to a notification gateway (PR #85).
- Automatic re-login, built and installed with its timer disabled (PR #88 and the sign-in driver
  commits that followed).
- Bridge Chrome kept usable on the kiosk box: install of the bridge scripts on every deploy, keyring
  unlock from a TPM-bound credential, and a GNOME Shell extension that keeps the bridge window below the
  kiosk (2026-10-04).

### Deployment

- `deploy/Deploy-ToLinux.ps1` no longer overwrites the box's `appsettings.Production.json` and checks
  the result of every native command (PR #84), and runs a pre-flight gate (PR #86).
- Each deploy installs the bridge, session-alarm and auto-relogin files and then runs
  `deploy/check-installed-drift.sh` to confirm the installed copies match the repo.

## Known limitations

- **Decline does not stop the linked cell phone ringing.** Google Voice rings linked phones as separate
  legs; the `603 Decline` on RotaryPhone's leg does not cancel them. The owner accepted this behaviour
  on 2026-10-04.
- **The phone depends on a signed-in browser session.** If Google signs the bridge Chrome out, SIP
  registration fails and calls stop until someone signs in again. The alarm reports it; automatic
  re-login exists but is not enabled.
- **Bluetooth HFP is not the active path.** The HFP code remains, but the deployed box takes calls from
  Google Voice and has no phone connected over HFP.
- **SMS send and mark-read are off by default** (`EnableSmsSend`, `EnableMarkRead`).

The current list of defects, with history, is [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md).

## Possible future work

None of these is scheduled. Each is grounded in a plan or an open note in this repository.

| Item | Source | State |
|---|---|---|
| Enable automatic re-login on the box | [docs/gv-relogin-driver-contract.md](docs/gv-relogin-driver-contract.md), [docs/SETUP-GVBridge.md](docs/SETUP-GVBridge.md) | Installed, timer disabled; owner decision |
| Reachable re-auth: a human fallback when automatic re-login cannot recover | [docs/plans/gv-reachable-reauth.md](docs/plans/gv-reachable-reauth.md) | Planned, not started |
| Build stamp, so the running build can be identified from the API | [docs/plans/build-stamp-and-deploy-verification.md](docs/plans/build-stamp-and-deploy-verification.md) | Planned, not built |
| Deploy tooling Task 6: print the post-deploy `BluetoothAdapter` / `UseActualBluetoothHfp` values so a config clobber is visible | [deploy-tooling plan](docs/archive/deploy/deploy-tooling-honest-deploy-plan.md) | Never started |
| Deploy tooling Task 11: record the watchdog-timer decision (keep the timer) as a comment above `daemon-reload` in the deploy | same | Never started; no code change needed beyond the comment |
| Deploy tooling Tasks 10 and 12: run the bridge installer from the deploy behind a gate, then an owner-run on-box check | same | Never started as written. The deploy now runs the narrower `deploy/install-gv-bridge.sh` with its own Chrome-flag gate, so these may be partly or wholly superseded |
| Decide whether the HFP profile should stay registered on Radio Console's music adapter | [docs/prompts/2026-09-07-hfp-on-music-adapter-drops-the-phone.md](docs/prompts/2026-09-07-hfp-on-music-adapter-drops-the-phone.md) | Open question from Radio Console |
| Find out whether Google Voice's own web Decline stops the linked cell, and copy it if so | [docs/prompts/2026-10-04-radioconsole-decline-on-cell-reply.md](docs/prompts/2026-10-04-radioconsole-decline-on-cell-reply.md) | Not pursued |
| Push-based message events from Google's signaler channel instead of polling | [docs/api-research/signaler-subscriptions-todo.md](docs/api-research/signaler-subscriptions-todo.md) | Research notes only |
