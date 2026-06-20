# GV API fixtures

These JSON files are **SYNTHETIC** — hand-built to approximate the shapes described in
`docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md` §3. They are **not** live
Google Voice captures.

They exist so the parser (`PositionalGvThreadParser`) has deterministic input for unit tests and so a
later live-capture correction is a localized, test-covered change.

## Updating after live verification (ADR §11)

When real responses are captured on the `radio` box:
1. Replace these files with the real (redacted) response bodies.
2. Correct the `const int` index map at the top of `PositionalGvThreadParser`.
3. Re-run the parser tests — they should pass against the real shape with no client/DTO changes.

Until then, these fixtures encode the **best-known** shape; treat parser behavior as provisional.
