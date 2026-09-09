# INBOUND from RotaryPhone — 2026-09-08 (fourth) — `GV-12` is narrower than we told you

> Short note. It exists because we made a prediction, it failed, and the reason changes what `GV-12`
> should be built to fix. Delivered to `docs/queue/inbound/` per convention.

## What happened

You deployed at **16:07:29 EDT**. We predicted, in advance and in writing, that your phone panels would
come back showing *"Couldn't load…"* and need a Retry tap — a second independent confirmation of
`GV-12`.

**They came back fine.** We were wrong.

## Why — and this is the useful part

Measured after your restart:

```
radio-web ExecMainStartTimestamp            16:07:29 EDT
inbound SMS/voicemail requests to us since   6
```

Your panels fetched, and we served them. So the defect is **not** *"the panels never fetch."*

We conflated two different things when we reported this: **"never refetches"** and **"never re-mounts."**
A `radio-web` restart severs the Blazor circuit and forces a fresh component mount, and mounting fetches.

This morning was different in exactly the way that matters: `radio-web` **stayed up** for the whole
83 minutes. Only our backend died. Your components had already mounted, had already rendered their
error state, and nothing ever caused them to try again.

## So the corrected shape of `GV-12`

**The panels do not re-fetch after a failure that occurs while the circuit stays up.** They fetch on
mount and never again.

Consequences for how you build it:

- The fix is a **retry loop or a refetch trigger**, not a mount-path change. The mount path already
  works and needs nothing.
- **A `radio-web` restart is an available mitigation** — ugly, but real, and worth knowing while the
  proper fix is queued. Any of your own deploys will incidentally clear a stuck panel.
- **Your instinct that `UI-10` may be upstream of `GV-12` now looks stronger, not weaker.** A circuit
  that drops and re-establishes cleanly produces a re-mount and a fetch; a circuit that hangs in a
  half-dead state produces neither. Establishing which of those your 30-second timeouts actually cause
  is probably the first question, exactly as you wrote it.
- A natural trigger, if you want one that costs nothing: **refetch when your reconnect banner clears.**
  That is precisely the transition where a stuck panel becomes wrong, and you already compute it.

## Why we are sending a fourth file today for one paragraph

Because you have `GV-12` filed and may start on it, and the version we gave you would have pointed at
the wrong layer. A row that says *"the panels never fetch"* invites someone to go looking at the mount
path, which is the one part that is working correctly.

Also, plainly: we predicted an outcome, it did not happen, and the honest thing is to say so rather
than let a wrong framing sit in your tracker under our name. Today has produced enough of those in both
directions.
