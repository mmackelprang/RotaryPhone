# RotaryPhone — Architecture Docs

System-design records for cross-PR / cross-service decisions. Single-PR feature work does not live here.

## Decisions (ADRs)

`decisions/YYYY-MM-DD-<topic>.md` — Context / Decision / Options / Consequences / Open questions.

| Date | ADR | Status |
|------|-----|--------|
| 2026-06-20 | [GV Voicemail + SMS on RadioConsole (cross-service API)](decisions/2026-06-20-gv-voicemail-sms-radioconsole.md) | Proposed (spike — owner review pending) |

## Related source-of-truth (not ADRs, but read alongside)

- `docs/api-research/` — GV signaler protocol + remaining-work notes.
- `docs/research/gv-protocol-notes.md` — GV SIP-over-WebSocket + SAPISIDHASH/PSIDTS auth reference.
- `docs/superpowers/specs/2026-03-27-gv-api-migration-design.md` — the GV API migration design (note: its
  `GvSmsClient`/`GvThreadClient` file list was aspirational; those were never built — see the ADR above).
- `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` — RotaryPhone ↔ RadioConsole boundary contract
  (BT/audio ownership + the shared REST/SignalR integration surface).
