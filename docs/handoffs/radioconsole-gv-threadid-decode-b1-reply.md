# Reply — B1: `%2F` thread ids are decoded on both thread routes

> Copy everything below the line and paste it into the RadioConsole session/agent.
> It is self-contained: the RadioConsole repo does not need to see the RotaryPhone repo.
> This answers **B1 only** of `docs/prompts/radioconsole-gv-threadid-decode-and-auth-blackout-request.md`.
> **B2 (the 20-minute auth blackout) is a separate change and gets its own reply** — nothing here
> touches the cookie/PSIDTS refresh path, so keep testing this surface inside a healthy window until
> that one lands.

---

## TL;DR

**B1 is fixed, in both routes, and the sanity check is in.** Your reproduction now returns messages
instead of an empty list, and mark-read on a group thread actually marks.

- `GET  /api/gvbridge/sms/threads/{threadId}` — decoded ✅
- `POST /api/gvbridge/sms/threads/{threadId}/read` — decoded ✅
- Warning log when a thread resolves to **0 messages** — added ✅

**Nothing changes on your side.** Keep sending exactly what you send today (single
`Uri.EscapeDataString`-style escaping, so `%20` for the space and `%2F` for the slash). That is the
spelling we now handle. Do **not** switch to double-escaping or to a raw `/` — you already showed both
are worse, and the fix is built around the single-escaped form.

## What we changed

`GvSmsController` binds the route value as `rawThreadId` and decodes it **once**, at the top of each
action, before the id is used for anything:

```csharp
var threadId = DecodeThreadId(rawThreadId);   // Uri.UnescapeDataString, guarded on '%'
```

Binding the encoded value under a *different* name is deliberate: it makes it impossible for a later
edit to reach for the still-encoded value by habit. In `MarkThreadRead` that covers all four uses —
the thread lookup, the `ListMessagesAsync` enumeration, the `updateread` write, and the
`ReadStateChanged` broadcast payload.

**The ids that already worked are unaffected.** `t.32665` has no escape sequence.
`Uri.UnescapeDataString` is not form decoding, so a literal `+` in `t.+18019208129` stays a `+` and is
never turned into a space — and the `%2B` spelling of the same id lands on the identical thread. All
four spellings are covered by regression tests.

## What you will see differently

1. **Group/MMS threads return their messages.** The reproduction from your §B1.1, replayed against a
   real Kestrel + the real captured `api2thread/list` wire shape:

   ```
   t.32665                                       HTTP 200  messages=4    threadId='t.32665'
   t.%2B18019208129                              HTTP 200  messages=15   threadId='t.+18019208129'
   t.+18019208129                                HTTP 200  messages=15   threadId='t.+18019208129'
   g.Group%20Message.d5Mri%2FNrDUQgXNXNQehOfw    HTTP 200  messages=1    threadId='g.Group Message.d5Mri/NrDUQgXNXNQehOfw'
   ```

2. **`threadId` in the response body is now the DECODED id** — `g.Group Message.d5Mri/NrDUQgXNXNQehOfw`,
   the same string `GET /threads` gives you. Previously it echoed back whatever escaping you sent. If
   anything on your side keys off the echoed value, it now matches your thread list exactly. Worth a
   glance, but this should be strictly less surprising than before.

3. **`ReadStateChanged` carries the decoded thread id** too, for the same reason — so the event matches
   the thread you rendered.

4. **Mark-read on a group thread now posts a real `updateread` per message.** Before, the step-2 thread
   lookup missed the encoded id, so it was a 404 or a mark of nothing.

## The sanity check (your §B1.3)

Yes, added — at **Warning**, one line, no stack trace:

```
warn: GvSmsController[0] SMS thread g.Group Message.NOSUCH/Q resolved to 0 messages on thread fetch
      — id-escaping mismatch, or the thread's messages fall outside the fetched folder window
```

It fires on both routes (`thread fetch` / `mark-read`), only when the fetch and parse genuinely
**succeeded** and only the per-thread filter matched nothing — which is exactly the blind spot you
identified: `Succeeded` and `ShapeIsSane` both pass in that state, so this was the one 200-with-empty
class our honest-status guards could not see. It stays quiet on the happy path and on a 502.

It is deliberately **not** chatty: it fires per user action, never per poll, and it is a single line
with no exception — we took your note about journald churn and audio distortion on the N100 seriously.

This also covers the second path you called out: a thread whose messages fall outside the fetched
folder window. That one is *legitimately* 0 messages today and we have **not** changed its behavior —
it still returns 200 with an empty list. It is now merely visible in the log. If it turns out to be
real in practice, say so and we will treat "thread exists but its messages are outside the window" as
its own defect rather than folding it in here.

## What we did NOT change

- **The response shape.** A thread with genuinely 0 messages is still `200 { messages: [] }`. We
  considered making it a 404 or a coded error and decided against it inside this change — it would be a
  contract change landing in a bug fix, and your GV-8 error state is the right place for that decision.
  Say the word if you'd rather have a distinct signal.
- **The 2–3 upstream calls per thread open** you noted. Recorded, not addressed here.
- **Anything in the auth/cookie path.** See the B2 note at the top.

## On F-5 (the message bubble ending in a literal `...`)

You said one curl against a known long message would settle it once B1 landed. B1 has landed, so that
probe is unblocked — worth running before assuming a truncation bug. Note the mechanism you suspected
is real: per-thread messages are derived by filtering the whole SMS folder list, and folder-list
entries carry snippets. If the probe shows truncation, send it over as its own item and we'll look at
whether a per-thread fetch is needed.
