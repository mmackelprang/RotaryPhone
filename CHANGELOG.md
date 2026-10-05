# Changelog

All notable changes to RotaryPhone are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

1.0.0 is the first tagged release. It summarizes the system as it ships, condensed from the pull
request history since November 2025. Pull request numbers are given in parentheses for notable
items. Earlier designs that were superseded (a Raspberry Pi host, a Windows NUC host, a Chrome
extension bridge, a real Bluetooth HFP voice path) are described in [docs/archive/](docs/archive/README.md).

## [Unreleased]

## [1.0.0] - 2026-10-04

### Added

**HT801 and SIP**

- SIPSorcery-based SIP server that a Grandstream HT801 analog telephone adapter registers with, so a
  rotary phone can ring, take pulse-dialed digits and carry two-way audio (#2, #4, #5).
- RTP audio bridge with G.711 PCMU, with RTP ports negotiated from the HT801's SDP (#5, #24, #34).
- The HT801 address is learned from its SIP REGISTER rather than hard-coded, resolved once, and
  reported honestly in status (#67, #68). See [docs/HT801-ADDRESS.md](docs/HT801-ADDRESS.md).
- Call history persisted to SQLite, so it survives restarts and deploys (#44).
- Optional VoIP.ms SIP trunk path (`GVTrunk`), skipped when no credentials are configured (#49).

**Google Voice call path**

- Calls placed and received through Google Voice using SIP over WebSocket with DTLS-SRTP audio,
  driven by the Google Voice HTTP API (#16, #17, #19). This replaced the earlier CDP and Chrome
  extension approaches (#12 to #15).
- Outbound calls routed through Google Voice, with the bridge and in-call state deferred until the
  far end answers and a 45-second no-answer timeout (#30, #38).
- Inbound calls are answered on handset lift (deferred answer), so a caller who hangs up before
  answer stops the rotary ringer (#39, #95). Two earlier attempts were reverted (#40/#41, #45/#46).
- `POST /api/phone/decline` declines a ringing call; the Google Voice leg is rejected with
  `603 Decline` (#94, #96).

**Google Voice messaging (for Radio Console)**

- Voicemail REST API with an audio proxy and cache (#54, #56).
- SMS thread read, background polling and SignalR push of new messages and voicemail (#57).
- SMS send, `POST /api/gvbridge/sms/send`, shipped disabled behind `EnableSmsSend` (#60).
- Mark-read and durable read state, shipped disabled behind `EnableMarkRead` (#62, #64).
- Group and MMS conversation ids are decoded correctly, and thread lists are parsed against the
  real captured wire format (#69, #70).

**Google Voice authentication and the bridge browser**

- Cookie management API, CDP-based cookie refresh from the bridge Chrome, and PSIDTS cookie
  rotation (#17, #21, #22, #72, #78).
- WebSocket keep-alive, automatic reconnect and recovery from 401s by rotating cookies (#36).
- Registration resilience: honest status, an end to re-register storms, automatic cookie recovery,
  a watchdog, and an escalating cooldown after 603/403 responses (#48, #65).
- GV bridge launch and liveness scripts under version control, with systemd user timers for the
  watchdog and the nightly restart (#75).
- GV session alarm: alerts a human when the Google Voice session is signed out, using the signal
  the service already reports (#85, #87, #90).
- Automatic re-login with a circuit breaker, keyring unlock from a TPM-held credential, bridge
  scripts installed by every deploy, and a GNOME Shell extension that keeps the bridge window
  below the kiosk (#88).

**Radio Console integration**

- REST and SignalR integration surface used by Radio Console, which shares the same Ubuntu box.
  Bluetooth adapter and audio ownership between the two services is defined in
  [docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md](docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md) (#7).
- Bell-failure tracking and a system-status contract served from the probe cache, with bell
  dismissals persisted and acknowledgements idempotent (#77, #79).
- Diagnostics endpoints reporting SIP registration and cookie validity (#20).

**Bluetooth (HFP and PBAP)**

- BlueZ HFP support, PBAP caller-name resolution, multi-phone pairing UI and an SCO audio bridge
  (#8, #10, #11). On the production box the Bluetooth stack runs, but no phone is
  connected over HFP on RotaryPhone's adapter (`hci1`): the phone is paired with Radio
  Console's adapter, and the cross-adapter guard refuses it. Calls go over Google Voice.

**Deploy and operations**

- `deploy/Deploy-ToLinux.ps1` cross-compiles, syncs to the box and restarts the systemd service,
  with a pre-flight check of the interpreter, SSH transport and sudo (#50, #84, #86).
- Drift checks that compare the box's installed user-level tooling against what was shipped, and
  shell test harnesses under `deploy/tests/` (#84, #85, #88).

### Changed

- Ported from the original Windows target to Linux for co-hosting with Radio Console (#7, #8).
- The default call mode is Google Voice API (`DefaultMode: "GVApi"`).
- Unmatched `/api/*` routes return a JSON 404 instead of the single-page app shell (#80).
- The `psidtsAgeSeconds` status field was removed; use `psidtsMintedAtUtc` (#79).
- A failed refresh-from-browser records its outcome instead of reporting success (#92).
- The bridge Chrome runs with `--disable-backgrounding-occluded-windows` and hides its
  crash-restore dialog (#93, #88).

### Fixed

- Hang-up teardown: SIP BYE sent to Google Voice with the correct CSeq, session race resolved and
  media torn down (#25 to #29).
- Outbound two-way audio restored by sending `Content-Type: application/sdp` and a To-tag on the
  HT801 200 OK (#35); digit and INVITE ordering from the HT801 (#31, #33).
- Audio bridge not starting on answered Google Voice calls (#23).
- Voicemail routes return 502 during an authentication blackout instead of 404 (#76).
- Deploys no longer report success without doing the job, and no longer overwrite the box's
  `appsettings.Production.json` (#50, #84, #86).
- Google Voice call notifications are suppressed on the kiosk (#37).

### Security

- Inter-service authentication gate: when `GVBridge:InterServiceAuthKey` is set, every
  `/api/gvbridge/*` request and the SignalR hub require an `X-RotaryPhone-Auth` header. It is off
  by default for LAN use (#61). The unauthenticated `/api/gvbridge/event` exemptions were removed (#81).
- The HT801 admin password was removed from tracked configuration in 1.0.0. Set it on the box
  only (#98).

### Known limitations

- Declining a call stops the rotary ringer but not the forwarded cell phone. Google Voice rings
  both legs separately, and the `603 Decline` on the RotaryPhone leg is not propagated to the cell,
  which rings until Google Voice sends the caller to voicemail. See
  [docs/prompts/2026-10-04-radioconsole-decline-on-cell-reply.md](docs/prompts/2026-10-04-radioconsole-decline-on-cell-reply.md).
- SMS send and mark-read are built but disabled by default (`EnableSmsSend`, `EnableMarkRead`).
- Google Voice access depends on a signed-in Chrome session on the box. Automatic re-login covers
  the routine case; a human fallback for when it cannot recover is designed but not built
  ([docs/plans/gv-reachable-reauth.md](docs/plans/gv-reachable-reauth.md), #89).
- The real Bluetooth HFP voice path is not used in production.
- Other open issues are tracked in [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md).

### Documentation

- Radio Console exchange records that had not been committed were added to the repository (#97).
- Release documentation sweep: release hygiene, archiving of development-process documents to
  `docs/archive/`, and a new CHANGELOG, CONTRIBUTING guide and docs index (#98, #99 and follow-ups).

[Unreleased]: https://github.com/mmackelprang/RotaryPhone/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/mmackelprang/RotaryPhone/releases/tag/v1.0.0
