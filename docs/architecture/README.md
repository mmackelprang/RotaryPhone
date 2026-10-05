# RotaryPhone — Architecture Docs

System-design records for cross-PR / cross-service decisions. Single-PR feature work does not live here.

## Decisions (ADRs)

`decisions/YYYY-MM-DD-<topic>.md` — Context / Decision / Options / Consequences / Open questions.

| Date | ADR | Status |
|------|-----|--------|
| 2026-06-20 | [GV Voicemail + SMS on RadioConsole (cross-service API)](decisions/2026-06-20-gv-voicemail-sms-radioconsole.md) | Implemented: read side #54, #56, #57; SMS send #60 (disabled by default, `EnableSmsSend`); inter-service auth gate #61. The ADR's own status line still reads "Proposed"; it predates the build |
| 2026-06-20 | [GV mark-read / durable read-state — contract ratification](decisions/2026-06-20-gv-markread-readstate-contract.md) | Accepted. Path A implemented in #64, shipped disabled by default (`EnableMarkRead`). The ADR's status line ("implementation held") predates that PR |
| 2026-07-29 | [HT801 address resolution — learned registrar bindings](decisions/2026-07-29-ht801-learned-registrar-binding.md) | Accepted (implemented in #68) |
| 2026-09-08 | [Bell-health contract (`XR-5`) — ratification + the transport-split defect](decisions/2026-09-08-bell-health-contract-ratification.md) | Accepted (contract already shipped; the authorized transport-split fix landed in #77; persistence and ack idempotency followed in #77 and #79) |
| 2026-09-08 | [`gv-bridge-ensure.sh` exit code — stays 0, not a health signal](decisions/2026-09-08-gv-bridge-ensure-exit-code.md) | Accepted. **Amended 2026-10-04** (§9): on a held lock the script waits up to 60 s (`flock -w 60`) and then runs the normal liveness check, so there is no third "lock held" exit-0 outcome; the script is now installed by every deploy |

## Related source-of-truth (not ADRs, but read alongside)

- `docs/HT801-ADDRESS.md` — the HT801 address: every location it can appear, the change procedure, and
  which verification signals are trustworthy (read alongside the 2026-07-29 ADR).
- `docs/api-research/` — GV signaler protocol notes (the March remaining-work list moved to `docs/archive/gv-call-path/remaining-work.md`).
- `docs/research/gv-protocol-notes.md` — GV SIP-over-WebSocket + SAPISIDHASH/PSIDTS auth reference.
- `docs/archive/gv-call-path/2026-03-27-gv-api-migration-design.md` — the GV API migration design (archived).
  `GvSmsClient` and `GvThreadClient`, listed there, were built later by the voicemail and SMS work
  (`src/RotaryPhoneController.GVBridge/Clients/`).
- `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` — RotaryPhone ↔ RadioConsole boundary contract
  (BT/audio ownership + the shared REST/SignalR integration surface).
