# GV API fixtures

`capture/` holds **REAL, LIVE-CAPTURED** Google Voice `api2thread/list` traffic — request bodies
verbatim, response bodies redacted. Captured 2026-07-31 via CDP against the authenticated Chromium
session on the `radio` box.

| File | Contents |
|---|---|
| `capture/{voicemail,messages,calls}.request.json` | Request bodies, **verbatim** (contain no PII) |
| `capture/{voicemail,messages,calls}.response.json` | Response bodies, **redacted** |

Each capture is 20 threads of the corresponding folder (voicemail=4, messages/SMS=2, calls=3).

## Read this before you touch anything here

These files replaced a set of **synthetic** fixtures that had been hand-built to match the shape the
parser assumed. The result was a parser that had never once seen a real Google response, a test suite
that was fully green, and a feature that returned nothing for weeks. Every test passed because every
fixture was written to agree with the code.

**The rule that follows from that: fixtures are evidence, not convenience.**

- **Never hand-edit a response fixture to make a test pass.** If the parser and the fixture disagree,
  the parser is wrong — or Google changed and you need a *new capture*. Editing the fixture destroys
  the only independent evidence in the test suite.
- **Never add a hand-written response fixture** alongside these. A synthesized fixture proves only
  that the parser agrees with whoever wrote it.
- Hand-written JSON is fine for *malformed-input* tests (wrong root type, truncated arrays) — those
  assert defensive behavior, not the wire contract, and are named accordingly.

## Redaction

Leaf values were replaced with **identical-length placeholders**; array positions, nesting depth, and
array lengths are preserved exactly, and that preservation was asserted programmatically at capture
time. Phone numbers became `+1555000xxxx` E.164 placeholders. This means every structural assertion
(index positions, array lengths, types, null-vs-present) is valid against these files; only the
literal string *contents* are not real.

Raw un-redacted captures stay on the box at `/home/mmack/uat-backup-20260731/capture/` and **must
never be committed** — they contain real phone numbers and voicemail transcripts.

## Re-capturing

If Google changes the wire shape, `CapturedWireShapeTests` fails. The fix is to re-capture, not to
adjust assertions:

1. Capture fresh responses via CDP against the authenticated session on `radio`.
2. Redact leaf values, preserving lengths and structure; assert the preservation programmatically.
3. Drop the redacted files in `capture/` and update the `const int` index map in
   `PositionalGvThreadParser`.
4. `CapturedWireShapeTests` re-derives the contract from the new files — if the indices are right, it
   goes green on its own.

## What is still UNVERIFIED

- **`GvThreadFolder.All`** — no capture taken. It has no wire value; `ToWireValue()` throws rather
  than guess, because a wrong folder integer returns another folder's records under a 200 OK.
- **Paging** — the capture was a single un-paged request. `root[2]` is a version cursor
  (`v1-1-<digits>`), *not* a demonstrated page token, and the request body's paging position is
  unknown. `ParseNextPageToken` returns null and `ListRawAsync` logs a warning if given a token.
