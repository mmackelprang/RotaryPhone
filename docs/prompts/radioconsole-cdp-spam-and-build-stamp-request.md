# Request from Radio Console → RotaryPhone: CDP log spam + build stamp

- **Date:** 2026-07-31
- **From:** Radio Console session (`D:\prj\RTest\RTest`), Planner
- **Origin:** a live UAT pass on the Ubuntu box (`radio`) after Radio Console merged GV-4 (durable GV mark-read) and a Tester flipped `GVBridge:EnableMarkRead` on.
- **Protocol:** boundary doc § "Passing Work Between Sessions" → *"If code changes are needed in RotaryPhone, create a file at `D:\prj\RotaryPhone\docs\prompts\`."*
- **Boundary doc Change Log:** deliberately **not** updated. That doc is scoped to BT/audio adapter ownership, and neither item below is a BT/audio boundary change. Flagging the choice so it reads as deliberate rather than skipped.
- **Nothing here is urgent-blocking for Radio Console.** Item 1 has a performance rationale; item 2 is deploy hygiene. Item 1 may be a symptom of something more serious — see §1.3.

---

## 1. `CdpCookieExtractor` log spam — and a concrete root-cause candidate

### 1.1 What we observe

The `rotary-phone` service logs, roughly every 20 minutes:

```
CDP: Cannot reach Chrome on port 9224
  (Connection refused)
  ... ~20 lines of stack trace ...
```

Cookies remain valid via another path, so **it is not fatal** and no user-facing GV feature is obviously broken by it alone.

### 1.2 Why we care (it is not tidiness)

Radio Console's project memory records a measured operational fact about this box:

> **Audio distortion correlates with SSH activity** — many/most distortion events happen when SSH sessions are querying logs (`journalctl`) or the SQLite DB. The Intel N100 has limited resources; SSH + journald + DB reads compete with the audio pipeline.

A ~20-line stack trace written every 20 minutes is continuous journald churn on a box where journald churn is a known contributor to audio distortion. So this is a **performance issue in log-noise clothing**, which is why we are raising it rather than ignoring a non-fatal warning.

**Minimum ask:** demote the recurring failure to a single-line Warning (no stack trace) and/or log-once-then-suppress-until-state-changes. That alone removes the churn.

### 1.3 A concrete root-cause candidate — please check this first

We think the extractor may be pointed at a port nothing is listening on, on this box:

- `GVBridgeConfig.ChromeCdpPort` defaults to **`9224`** — `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs:23`
- `CdpCookieExtractor` expects a Chrome instance with a live **Google Voice** tab. Your own tests encode that expectation: `CdpCookieRefreshTests.cs:93` fixtures a target with `"title":"Google Voice"`, `"url":"https://voice.google.com/u/0/calls"`, `webSocketDebuggerUrl = ws://127.0.0.1:9224/...`.
- The **only** Chrome that Radio Console's deploy launches on that box is the **kiosk**, on **port 9223**, pointed at `http://localhost:5002` (the Radio Console UI): `deploy/Deploy-ToLinux.ps1:363` —
  ```
  google-chrome --kiosk ... --remote-debugging-port=9223 http://localhost:5002
  ```

Different port, different instance, different purpose. **Unless something else on that box separately launches a Chrome with a Google Voice session on 9224, there is simply nothing there to connect to** — which would explain a *periodic, permanent, connection-refused* failure exactly like the one observed.

Please confirm on the box:

```bash
ss -ltnp | grep -E '922[34]'
curl -s http://localhost:9224/json/version   # expect: connection refused
curl -s http://localhost:9223/json/version   # expect: the kiosk Chrome
```

**To be explicit about ownership:** the kiosk Chrome on 9223 is Radio Console's and is *not* a GV session — please do **not** repoint the extractor at 9223 expecting to find Google Voice there. If the extractor needs a GV-session Chrome, that instance needs to be provisioned and supervised on your side. If you want it on a specific port or under a specific systemd unit, tell us and we will keep our kiosk out of the way.

### 1.4 Possible larger bug — this may not be standalone

A Radio Console Tester is currently investigating a **more serious** symptom: **GV read endpoints returning empty lists while reporting healthy.** We suspect a shared root cause with the above. Proposed causal chain, offered as a hypothesis to test rather than a conclusion:

1. CDP unreachable → the cookie **refresh** path is permanently dead.
2. Reads continue on cached cookies (the "another path" that keeps things nominally working).
3. Cached cookies eventually age out.
4. Google then returns a login/consent payload rather than data — **not** an HTTP error.
5. The thread/voicemail parsers find no items and yield **empty lists**.
6. Health reports fine, because `_areCookiesValid` is set from a health probe (`GVApiAdapter.cs:273/411/701`) rather than from "did the last read actually return data."

If that chain holds, the log spam and the empty-lists bug are the **same defect at two severities**, and fixing only the logging would suppress the one visible symptom of a real outage. **Please treat §1.3 as diagnosis, not just noise reduction** — and if it is confirmed, this item should be absorbed into the larger fix.

A useful hardening regardless of cause: make "healthy" mean *the last read returned parseable data*, so a silent auth expiry cannot present as healthy-but-empty.

---

## 2. Build stamp for `rotary-phone`

### 2.1 Why

On 2026-07-29, `rotary-phone` was found running a **stale binary** after a deploy that restarted only `radio-api` / `radio-web`. Confirming which build was actually running required `strings`-grepping a ~130 MB single-file bundle for a known symbol.

A sibling incident on our side (2026-07-30, stale `radio-web`) silently invalidated a feature-flag flip that would otherwise have looked successful. Same class of failure, and the reason we are hardening our own side as queue item **OPS-1**.

### 2.2 What we did, if you want to mirror it

Not prescribing your design — this is what already works in `D:\prj\RTest\RTest`:

1. **Stamp the SHA at build time.** `Directory.Build.props` sets `SourceRevisionId` from `git rev-parse HEAD`; the .NET SDK appends it to `AssemblyInformationalVersion` as `<version>+<sha>`. Deploy also passes `-p:SourceRevisionId=<sha>` explicitly, so the fallback target is only for local builds.
2. **Expose it.** `GET /api/health/version` → `{ gitSha, gitShaShort, informationalVersion, assemblyVersion, buildTimestampUtc, assemblyName }`. One gotcha we hit: `Assembly.Location` is **empty** for `PublishSingleFile` builds — fall back to `Environment.ProcessPath`.
3. **Verify at deploy time and fail loudly.** `Deploy-ToLinux.ps1` polls the endpoint after restart and `exit 1`s on mismatch or unreachability. **This is the part that actually prevents the incident** — a stamp nobody checks would not have caught either failure.

Caveat worth copying: our `buildTimestampUtc` is file mtime, i.e. *landed-on-disk* time, not compile time. **The SHA is the authoritative signal**; treat the timestamp as a hint.

### 2.3 Open question you should resolve first — which tree is authoritative?

A Tester found that stack traces from your **deployed** build reference paths under:

```
D:\prj\rp-deploy\...
```

rather than `D:\prj\RotaryPhone\...`.

That suggests the deployed artifact may be built from a **different working tree** than the repo we read when we reconcile cross-service contracts. If so, that is a bigger problem than either item here: it would mean the source of truth for what is running is ambiguous, and Radio Console has been reading `D:\prj\RotaryPhone` to derive contracts (we did exactly that for ADR-028, the SMS send contract).

**Please resolve this before adding a stamp** — a build stamp derived from the wrong tree is worse than no stamp, because it manufactures false confidence. If `rp-deploy` is a legitimate staging/publish tree, say so and we will note it; if it is a stale copy, it should go.

---

## 3. What Radio Console is doing on its own side

For context, so you can see the split:

- **OPS-1** (queued) — add `/api/health/version` to `Radio.Web` (its assembly already carries the SHA; it is simply not exposed) and extend deploy verification to cover `radio-web`, which today is only checked with `systemctl is-active`.
- **GV-6** (queued) — distinguish your `409 markread_disabled` dark-state response from a genuine failure. **No change needed on your side**; we simply were not documenting the `409` and could not tell "feature dark" from "GV unreachable." ADR-024 §3.3 has been amended.

Neither depends on this request.

## 4. Reply

Per protocol, reply in `D:\prj\RotaryPhone\docs\handoffs\` (as with `radioconsole-gv-markread-reply.md`) and mention it in the Radio Console session. Most useful reply contents:

1. The `ss -ltnp | grep 922` output from the box — confirms or kills the §1.3 hypothesis immediately.
2. Whether the empty-lists bug shares the root cause (§1.4).
3. What `D:\prj\rp-deploy` is (§2.3).
