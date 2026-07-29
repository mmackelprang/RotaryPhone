# RotaryPhone — Architecture Docs

System-design records for cross-PR / cross-service decisions. Single-PR feature work does not live here.

## Decisions (ADRs)

`decisions/YYYY-MM-DD-<topic>.md` — Context / Decision / Options / Consequences / Open questions.

| Date | ADR | Status |
|------|-----|--------|
| 2026-06-20 | [GV Voicemail + SMS on RadioConsole (cross-service API)](decisions/2026-06-20-gv-voicemail-sms-radioconsole.md) | Proposed (spike — owner review pending) |
| 2026-07-29 | [HT801 address resolution — learned registrar bindings](decisions/2026-07-29-ht801-learned-registrar-binding.md) | Accepted (implemented in PR2) |

## Related source-of-truth (not ADRs, but read alongside)

- `docs/HT801-ADDRESS.md` — the HT801 address: every location it can appear, the change procedure, and
  which verification signals are trustworthy (read alongside the 2026-07-29 ADR).
- `docs/api-research/` — GV signaler protocol + remaining-work notes.
- `docs/research/gv-protocol-notes.md` — GV SIP-over-WebSocket + SAPISIDHASH/PSIDTS auth reference.
- `docs/superpowers/specs/2026-03-27-gv-api-migration-design.md` — the GV API migration design (note: its
  `GvSmsClient`/`GvThreadClient` file list was aspirational; those were never built — see the ADR above).
- `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` — RotaryPhone ↔ RadioConsole boundary contract
  (BT/audio ownership + the shared REST/SignalR integration surface).
