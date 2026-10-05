# Plan — Follow-ups: Outbound `InCall` ordering + RotateCookies request shape

Status: DRAFT (implementation plan only — no production code written here)
Author: Claude Planner
Date: 2026-06-13
Base: `main` @ `8be3b98` (includes PR #34 outbound-RTP-from-INVITE-SDP, PR #35 HT801 `Content-Type`, PR #36 GV WS keep-alive/reconnect/401-recovery)

## TL;DR — PR split recommendation

**Ship as TWO PRs.**

- **PR-A (Item A — Outbound premature-`InCall` ordering):** self-contained code fix, fully unit-testable with mocks, ready to implement and UAT now. No external dependency.
- **PR-B (Item B — RotateCookies request shape):** **research-gated.** The exact `RotateCookies` request shape for the `voice.google.com` origin is unconfirmed and CANNOT be implemented correctly without a packet capture from the live account. Blocking PR-A on a capture session would be wrong. PR-B should be a small, separate PR landed AFTER the capture (the capture itself is queued as a research spike).

Rationale for the split (beyond "different in nature"):
1. **Different risk surfaces.** PR-A touches the call state machine we just UAT-verified (high-blast-radius, conservative). PR-B touches only the best-effort cookie-rotation seam, which already fails safe to the CDP fallback (low blast radius). Mixing them means a state-machine regression and a cookie experiment share one revert unit.
2. **Different readiness.** PR-A is implementable today; PR-B is blocked on a capture that needs the live account on `radio` (operator-gated, see Open Questions).
3. **Different test strategy.** PR-A is pure mock-based unit tests + live call UAT. PR-B is a recorded-fixture replay test that can't be written until the fixture exists.

---

## Item A — Outbound premature-`InCall` ordering

### Problem statement (with file:line citations)

On an OUTBOUND call (rotary handset → cell), `CallManager.PlaceGvCallAsync` starts the audio bridge and flips state to `InCall` at call PLACEMENT, roughly 6–10 s before the far end actually answers:

- `src/RotaryPhoneController.Core/CallManager.cs:610-638` — `PlaceGvCallAsync`:
  - `:614` `var callId = await _boundAdapter!.PlaceCallAsync(number);` — returns as soon as the SIP INVITE is accepted/queued, NOT when the callee answers.
  - `:628` `await _boundAdapter.OnCallAnsweredOnRotaryPhoneAsync();` — starts the HT801↔GV audio bridge immediately.
  - `:630-631` `CallStartedAtUtc = DateTime.UtcNow; CurrentState = CallState.InCall;` — flips to `InCall` at placement time.

The genuine "callee answered" signal already exists end-to-end but is currently ignored for outbound:

- `src/RotaryPhoneController.GVBridge/Sip/GvSipTransport.cs:1161-1162` — on the INVITE `200 OK`, sets `invSession.Status = CallStatusType.Active` and raises `CallStatusChanged(..., CallStatusType.Active)`. **This is the true answer event.**
- `src/RotaryPhoneController.GVBridge/Adapters/GVApiAdapter.cs:246-249` — `CallStatusChanged` handler: `if (e.NewStatus == CallStatusType.Active) OnCallAnswered?.Invoke();`
- `src/RotaryPhoneController.Core/CallManager.cs:154-156` — `_boundAdapter.OnCallAnswered += HandleAdapterCallAnswered;`
- `src/RotaryPhoneController.Core/CallManager.cs:166-169` — `HandleAdapterCallAnswered()` → `HandleCallAnsweredOnCellPhone()`.
- `src/RotaryPhoneController.Core/CallManager.cs:390-415` — `HandleCallAnsweredOnCellPhone()` **no-ops** for outbound because of the guard at `:394`:
  `if (CurrentState != CallState.Ringing) { _logger.LogWarning(...); return; }`
  By the time `Active` arrives, state is already `InCall` (set at `:631`), so the real answer handler logs "not in Ringing state" and returns.

Symptoms (already observed in debugging, both benign-but-undesirable):
1. A one-shot `errno-101` "Network is unreachable" cold-send blip: the audio bridge (`OnCallAnsweredOnRotaryPhoneAsync` at `:628`) starts pushing RTP toward GV before the DTLS-SRTP peer / far end is connected.
2. Potential early-audio clipping: we stream HT801→GV (and play GV→HT801) for the 6–10 s before the callee picks up.

### What this is NOT (regression guardrails)

- This is **NOT** the 0-RTP root cause. That was the HT801 `Content-Type: application/sdp` fix (PR #35, commit `32df5c0`) plus the outbound-RTP-port-from-INVITE-SDP fix (PR #34, commit `478d5ff`). Both are shipped and UAT-verified. **Do not touch `SIPSorceryAdapter.HandleInvite`, the `Content-Type` header, or `ExtractRtpDetailsFromSdp` — those produce the now-working two-way audio.**
- The negotiated-RTP capture must still happen BEFORE the bridge starts. Today `SetNegotiatedRtpDetails` is called at `CallManager.cs:620` inside `PlaceGvCallAsync`, fed by `HandleRtpDetailsNegotiated` (`:177-182`) which is wired from the HT801's INVITE SDP (PR #34). The deferral must preserve "capture negotiated RTP, THEN start bridge" ordering — the negotiated details are captured at placement, but only CONSUMED at answer.
- The HT801 sends **TWO INVITEs** per outbound call (registration-name `rotaryphone` then the real number; commit `9bbdb84`). That filtering lives in `SIPSorceryAdapter` (only the dialable-number INVITE fires `OnDigitsReceived`/`OnHookChange`) and in `CallManager.HandleDigitsReceived:234-238` (non-numeric guard). **This plan does not change that filtering** — by the time `PlaceGvCallAsync` runs, the real number has already been isolated. The plan only changes WHEN bridge-start + `InCall` happen relative to GV's `200 OK`.

### Design of the fix

Keep the call in `Dialing` after placement; defer bridge-start + `InCall` to the GV-answered path.

Two viable approaches were considered:

- **Approach A1 (RECOMMENDED): reuse `Dialing` as the outbound-ringing state.** After `PlaceCallAsync` succeeds, capture negotiated RTP and stay in `Dialing`. Add a private flag `_outboundCallPending` (or reuse `_currentCallHistory.Direction == Outgoing` + `Dialing` state) so the answer handler knows to start the bridge instead of cancelling a ringing INVITE. When `Active` arrives, start the bridge and flip to `InCall`.
  - Pro: no enum/state-machine surface change; smallest diff to a just-verified state machine; `Dialing` already means "outbound, not yet connected" for the BT path (see `HandleDeviceCallActive:449-458`, which does exactly this `Dialing → InCall` on `call_active`). This makes the GV path symmetric with the proven BT path.
  - Con: `Dialing` now covers both "collecting digits" and "placed, ringing"; acceptable because digits are already collected before `PlaceGvCallAsync` runs.
- **Approach A2: add a new `CallState.OutboundRinging` enum value.** Explicit, self-documenting.
  - Pro: clearer state semantics; dashboards could show "ringing".
  - Con: touches the `CallState` enum (`src/RotaryPhoneController.Core/CallState.cs`), every `switch`/UI consumer, the Blazor UI in the RTest repo (per memory `project_ui_integration.md` the active UI is in RTest, not this repo), and the GVTrunk dashboard. Larger blast radius on a conservative PR. **Defer to a later UX-driven PR if "ringing" indication is wanted.**

**Decision: implement A1.** It mirrors the already-proven BT outbound path (`HandleDeviceCallActive`) and keeps the diff minimal. (See Open Question 1 — confirm with user that no UI currently depends on outbound calls being `InCall` at placement.)

### The shape of the change (descriptive — Builder writes the code)

1. **`PlaceGvCallAsync` (`CallManager.cs:610-638`):** after a successful `PlaceCallAsync`, call `SetNegotiatedRtpDetails` (as today, `:620`) to stash the negotiated RTP, but DO NOT call `OnCallAnsweredOnRotaryPhoneAsync()` and DO NOT set `InCall`. Remain in `Dialing`. Set a private `bool _outboundConnectPending = true;` so the answer path can distinguish outbound-placed from inbound-ringing. Keep the existing `catch` that resets to `Idle` on placement failure (`:633-637`).

2. **`HandleCallAnsweredOnCellPhone` (`CallManager.cs:390-415`):** generalize the guard. Today it only handles `Ringing` (inbound-answered-on-cell). Add an outbound branch:
   - If `CurrentState == CallState.Dialing && _outboundConnectPending`: this is the GV `Active` for an outbound call. Start the audio bridge via `_boundAdapter.OnCallAnsweredOnRotaryPhoneAsync()` (the negotiated RTP was already stashed at placement), set `CallStartedAtUtc`, flip to `InCall`, clear `_outboundConnectPending`, update call history (`AnsweredOn` is N/A for outbound — leave as-is or add an `Outbound` semantic; do NOT regress inbound history). DO NOT call `CancelPendingInvite()` in this branch (that's the inbound-stop-ringing action; for outbound the HT801 INVITE was already answered with `200 OK` and must stay up).
   - If `CurrentState == CallState.Ringing`: keep the EXACT current behavior (inbound answered on cell — cancel pending INVITE, audio stays on cell, no bridge). Unchanged.
   - Otherwise (e.g. already `InCall`, or `Idle`): keep the warning + return (handles duplicate `Active` events / re-INVITE 200 OK at `GvSipTransport.cs:1133`).

3. **Idempotency / double-fire safety:** GV may emit `CallStatusChanged(Active)` more than once (e.g. re-INVITE `200 OK` reuses the 200-OK code path at `GvSipTransport.cs:1123-1162`). Guard the outbound branch on `_outboundConnectPending` so the bridge starts at most once; subsequent `Active` events fall through to the "already InCall" warning. Clear `_outboundConnectPending` in `HangUp` (`CallManager.cs:640-699`) alongside the other per-call resets (`:691-692`).

4. **No change to `StartCall` (`:555-601`)** beyond what `PlaceGvCallAsync` already does — it still routes to `PlaceGvCallAsync` for GV modes.

### Ordered tasks (PR-A)

- **A-T1 — Failing tests first (TDD).** Add to `src/RotaryPhoneController.Tests/CallManagerTests.cs` (Moq + xUnit; mirror existing idioms — `Mock<ICallAdapter>` + `mockAdapterRegistry.Setup(r => r.ActiveAdapter)` + `mockAdapter.Raise(a => a.OnCallAnswered += null)`):
  - `Outbound_AfterPlaceCall_StaysDialing_UntilAnswered`: GV adapter active; `HandleHookChange(true)` then `HandleDigitsReceived("9193718044")`; after `PlaceCallAsync` resolves, assert `CurrentState == Dialing` and `OnCallAnsweredOnRotaryPhoneAsync` was NOT yet invoked (`mockAdapter.Verify(a => a.OnCallAnsweredOnRotaryPhoneAsync(), Times.Never)`).
  - `Outbound_OnGvActive_StartsBridge_AndGoesInCall`: continue the above, then `mockAdapter.Raise(a => a.OnCallAnswered += null)`; assert `CurrentState == InCall`, `CallStartedAtUtc != null`, and `OnCallAnsweredOnRotaryPhoneAsync` invoked `Times.Once`.
  - `Outbound_OnGvActive_DoesNotCancelInvite`: assert `_mockSipAdapter.Verify(s => s.CancelPendingInvite(), Times.Never)` across placement + answer (the HT801 leg must stay up).
  - `Outbound_DuplicateGvActive_StartsBridgeOnce`: raise `OnCallAnswered` twice; assert `OnCallAnsweredOnRotaryPhoneAsync` invoked `Times.Once` and state stays `InCall`.
  - `Inbound_AnsweredOnCell_Unchanged`: keep/duplicate the existing `Bluetooth_OnCallAnsweredOnCellPhone_ShouldCancelInviteAndNotBridge` (`CallManagerTests.cs:124-139`) to prove inbound-answered-on-cell still cancels the INVITE and does NOT bridge.
  - Update existing `HandleDigitsReceived_WhileIdle_ShouldImplicitOffHookAndStartCall` (`CallManagerTests.cs:204-234`) expectation if needed: it asserts `NotEqual(Idle)` and `PlaceCallAsync Times.Once`. With A1, post-placement state is `Dialing` (still `NotEqual(Idle)`), so this test should still pass unmodified — verify, don't pre-emptively change.
- **A-T2 — Implement A1** in `CallManager.cs` per "shape of the change" above. Add `_outboundConnectPending` field; modify `PlaceGvCallAsync` and `HandleCallAnsweredOnCellPhone`; reset the flag in `HangUp`.
- **A-T3 — Run the full unit suite** (`dotnet test src/RotaryPhoneController.Tests` and `src/RotaryPhoneController.GVBridge.Tests`) — green, including the untouched inbound/BT tests. Per memory `feedback_deploy_all_dlls.md`, ensure the build publishes all DLLs for the radio deploy.
- **A-T4 — Update docs:** `docs/KNOWN-ISSUES.md` (remove/annotate the `errno-101` cold-send blip once UAT confirms it's gone) and a one-line note in `docs/SETUP-AND-TESTING.md` outbound-call section that outbound now transitions `Dialing → InCall` on GV answer.
- **A-T5 — Branch + PR:** branch `fix/outbound-incall-ordering`, PR to `main` with a Docs Impact section and the live-UAT checklist below filled in.

### TDD test plan (PR-A) — state-transition matrix

| Scenario | Trigger sequence | Expected state path | Bridge start | CancelPendingInvite |
|---|---|---|---|---|
| Outbound placed, not yet answered | off-hook → digits → `PlaceCallAsync` resolves | `Idle → Dialing` (stays) | NO | NO |
| Outbound answered (GV `Active`) | …then `OnCallAnswered` | `Dialing → InCall` | YES (once) | NO |
| Outbound duplicate `Active` | `OnCallAnswered` ×2 | `Dialing → InCall` (idempotent) | once total | NO |
| Outbound placement fails | `PlaceCallAsync` throws | `Dialing → Idle` (existing catch) | NO | NO |
| Inbound answered on cell | `SimulateIncomingCall` → `OnCallAnsweredOnCellPhone` | `Ringing → InCall` | NO | YES |
| Inbound answered on rotary | `SimulateIncomingCall` → `HandleHookChange(true)` | `Ringing → InCall` | YES (rotary) | NO (ring already up) |
| Hang up mid-outbound-ring | off-hook → digits → on-hook before `Active` | `Dialing → Idle`, `_outboundConnectPending` cleared | NO | YES (teardown) |

All of the above are unit-testable with mocks — no live stack required.

### Live-UAT checklist (PR-A) — run on `radio` after deploy (operator-driven; Planner/Builder do not ssh)

1. **Outbound two-way audio still works** (PRIMARY non-regression): lift handset, dial a real cell, callee answers → confirm clear two-way audio both directions. (Guards against regressing PR #34/#35.)
2. **No early audio / clipping:** between dialing and callee-answer, the rotary earpiece should NOT carry GV audio and the bridge should not be streaming. After answer, audio starts cleanly with no clipped first syllable.
3. **`errno-101` gone or benign:** tail the gv-bridge log during an outbound call; the one-shot `errno-101` cold-send at placement should no longer appear (bridge now starts at answer). If it still appears, confirm it's a single benign blip and note it.
4. **State timing:** log/observe that `State changed to: InCall` (logged at `CallManager.cs:41`) now appears at callee-answer time, not ~6–10 s earlier at placement.
5. **Inbound unaffected:** receive an incoming call, answer on rotary → two-way audio. Receive an incoming call, answer on the cell → rotary stops ringing, audio stays on cell. (Both must match pre-PR behavior.)
6. **Hang-up mid-ring:** dial out, hang up BEFORE the callee answers → clean return to Idle, no stuck bridge (`/api/gvbridge/status` shows not in-call; `GET radio:5004/api/gvbridge/status`).
7. **Status endpoint sane:** `GET radio:5004/api/gvbridge/status` before/during/after — `sipRegistered: true` throughout; no spurious availability flap.

### Risks (PR-A)

- **R1 (HIGH-attention):** touches the call state machine just UAT-verified. Mitigation: A1 mirrors the proven BT outbound path; inbound paths are explicitly unchanged; full state-matrix unit tests + the live-UAT non-regression items 1/5 gate the merge.
- **R2:** if GV ever fires `CallStatusChanged(Active)` LATE or NEVER for some carriers (e.g. straight to media without a 200-OK we observe), the call would hang in `Dialing` and never bridge. Mitigation: keep the existing 60 s ringing-timeout pattern in mind — consider an outbound-dialing timeout that resets to Idle (the inbound path already has one at `CallManager.cs:342-356`). **See Open Question 2** — do we want a Dialing timeout for outbound, and if so what duration. (Recommend yes, ~45 s, but flag as decision.)
- **R3:** `_outboundConnectPending` must be reset on every exit path (hangup, placement failure, answer) or a subsequent inbound call could mis-route. Mitigation: reset in `HangUp` and in the answer branch; unit test `Hang up mid-outbound-ring`.

---

## Item B — RotateCookies request shape (research-gated)

### Problem statement (with file:line citations)

PR #36 shipped a best-effort browser-less PSIDTS refresh seam, but the EXACT `RotateCookies` request shape for the `voice.google.com` origin is UNCONFIRMED:

- `src/RotaryPhoneController.GVBridge/Auth/GvCookieRotator.cs:13-21` — the class TODO states the body/Referer/Origin/key for `voice.google.com` are NOT confirmed from a capture; public references are from the Bard/Gemini origin.
- `GvCookieRotator.cs:25` — `RotateCookiesUrl = "https://accounts.google.com/RotateCookies"` (URL guessed from Gemini libs).
- `GvCookieRotator.cs:46` — body `"[000,\"-0000000000000000000\"]"` is a placeholder "poke", not a confirmed payload.
- `GvCookieRotator.cs:49-51` — sends `Cookie: <ToCookieHeader()>`, `Origin: https://voice.google.com`, `Referer: https://voice.google.com/` — all unverified for this endpoint.
- The seam is wired and safe: `GVApiAdapter.cs:485-495` calls `TryRotateCookiesAsync` as PRIMARY 401-recovery, falling back to `ReloadCookiesAsync` + CDP `refresh-from-browser` on any failure (`:497-506`). So today, if the request shape is wrong, it returns `NotRotated` and the CDP fallback carries the load. The goal is to make the browser-less path actually succeed so we stop leaning on CDP/Chrome.

> **DOC DISCREPANCY (Open Question 3):** the task cites `docs/research/gv-protocol-notes.md §3.2/§5.1`, and `GvCookieRotator.cs:21` references the same path. **That file does not exist** in the repo — `docs/research/` is absent; protocol research lives under `docs/api-research/` (`signaler-protocol.md`, `remaining-work.md`, etc.). Either the notes file needs to be created at `docs/research/gv-protocol-notes.md` (and the §3.2/§5.1 sections written) or the code comment + this plan should point at the real `docs/api-research/` location. Flagging rather than guessing.

### The blocker

You cannot tighten `GvCookieRotator` correctly without the ACTUAL request captured from a logged-in `voice.google.com` session. Everything else (overlay onto `GvCookieSet`, fixture test) is mechanical once the capture exists.

### Capture method — candidates and recommendation

The running gv-bridge already drives a Chrome with CDP on **port 9224** (`GVBridgeConfig.cs:23` `ChromeCdpPort = 9224`; CDP `Network.getCookies` already used by the controller's `refresh-from-browser` at `GVBridgeController.cs:246-315`). That existing CDP wiring is the cheapest capture surface.

- **Candidate 1 (RECOMMENDED) — CDP `Network.*` domain capture against the existing port-9224 Chrome.** Enable the `Network` domain over the existing CDP WebSocket and record `Network.requestWillBeSent` / `Network.requestWillBeSentExtraInfo` / `Network.responseReceived` / `Network.responseReceivedExtraInfo` events, filtered to any request whose URL contains `RotateCookies` (or `accounts.google.com` rotation traffic), while a real cookie rotation happens (trigger by letting a `sipregisterinfo/get` 401 occur, or by navigating the GV tab so Chrome itself rotates PSIDTS).
  - Feasible headless on `radio`: YES — same transport the bridge already uses; no GUI needed. The Chrome is already running with `--remote-debugging-port=9224`.
  - Extract: full request URL (confirm host: `accounts.google.com` vs an `*.clients6`/`voice` host), HTTP method, ALL request headers (esp. `Origin`, `Referer`, `Content-Type`, any `X-*` / `X-Same-Domain` / `x-goog-*` / `Authorization`/`SAPISIDHASH`, `Google-Accounts-XSRF`), the request body (exact bytes — confirm whether it's the `[000,"…"]` array or something else and what the numeric fields are), the response status, and the `Set-Cookie` response headers (names + attributes for `__Secure-1PSIDTS` / `__Secure-3PSIDTS`, and whether `__Secure-1PSIDCC`/`SIDCC` also rotate). NOTE: `responseReceivedExtraInfo` is required to see `Set-Cookie` (raw response headers); the plain `responseReceived` redacts them.
  - One caveat: CDP `Network.getResponseBody` for a redirect/`Set-Cookie`-only response may be empty; rely on the `*ExtraInfo` events for headers.
- **Candidate 2 — mitmproxy / DevTools "Save as HAR".** Point Chrome (or the box) through mitmproxy, log the rotation, export HAR.
  - Feasible headless: PARTIAL — needs a proxy + Google's cert pinning tolerance; more setup than Candidate 1. Good as a cross-check if CDP capture is ambiguous.
- **Candidate 3 — instrument the existing CDP path in-process.** Add a temporary debug log in the bridge that dumps the request/response when `Network` events fire. Most code; only worth it if Candidates 1/2 can't trigger a rotation on demand.

**Recommendation: Candidate 1.** Smallest setup, reuses the port-9224 Chrome, headless-friendly on `radio`. Capture once, sanitize, save as a fixture.

### What to record into the fixture (sanitized)

Create `src/RotaryPhoneController.GVBridge.Tests/Auth/Fixtures/rotatecookies-request.json` and `…-response.json` containing:
- Request: method, absolute URL, header set (with secret VALUES redacted but NAMES preserved — the test asserts on header names + static values like `Origin`, not on secrets), and the body template (with cookie/token values redacted).
- Response: status code, and the `Set-Cookie` lines for `__Secure-1PSIDTS` / `__Secure-3PSIDTS` (values can be synthetic `FRESH1`/`FRESH3` as the existing test already does — `GvCookieRotatorTests.cs:50-51`).

**No real cookies/tokens land in the repo.** (Open Question 4 — confirm the redaction policy and that the fixture is acceptable to commit.)

### Ordered tasks (PR-B) — split into a spike + a code PR

**B-Spike (research, no production code, operator-gated):**
- **B-S1 — Capture** the live `RotateCookies` request/response on `radio` via Candidate 1 (CDP `Network.*` on port 9224). Owner = the operator who has the live Google session (see Open Question 5). Planner/Builder cannot do this (no ssh, no live account).
- **B-S2 — Document** the confirmed shape in `docs/research/gv-protocol-notes.md` §3.2 (request) / §5.1 (response), creating the file if it doesn't exist (resolves Open Question 3), and sanitize → commit the fixture files.

**PR-B (code, depends on B-Spike):**
- **B-T1 — Failing fixture test first (TDD).** Extend `src/RotaryPhoneController.GVBridge.Tests/Auth/GvCookieRotatorTests.cs` (reuse the existing `StubHandler` at `:29-33`): assert the rotator emits the CONFIRMED request — correct URL, method, `Origin`/`Referer`/`Content-Type`, any required `X-*` header, and body — by inspecting the `HttpRequestMessage` the stub receives, and parses `__Secure-1PSIDTS`/`-3PSIDTS` from the recorded `Set-Cookie` response. Add a negative test that a malformed/empty rotation still returns `NotRotated` (already covered at `:62-94`; keep).
- **B-T2 — Tighten `GvCookieRotator`** (`GvCookieRotator.cs`) to the confirmed shape: fix `RotateCookiesUrl` if wrong (`:25`), set the real body (`:46`), add/correct required headers (`:49-51`), keep the best-effort/non-throwing contract (`:90-98`) and the `Set-Cookie` PSIDTS parse (`:64-73`).
- **B-T3 — Verify the overlay path** `GvCookieSet.WithRefreshedPsidts` (`src/RotaryPhoneController.GVBridge/Auth/GvCookieSet.cs:53-77`) and `SpliceCookie` (`:84-94`) correctly splice the freshly-captured cookie NAMES (confirm the capture didn't reveal additional rotating cookies like `__Secure-1PSIDCC`/`SIDCC` that also need overlaying — if so, extend `WithRefreshedPsidts` + `CookieRotationResult` and add a test). No change to `GVApiAdapter.TryRotateCookiesAsync:523-563` unless new cookie fields are added.
- **B-T4 — Run** `dotnet test src/RotaryPhoneController.GVBridge.Tests` green.
- **B-T5 — Docs + PR:** update `GvCookieRotator.cs` class comment to drop the "UNCONFIRMED" TODO and cite the now-real notes file; branch `fix/rotatecookies-request-shape`; PR to `main` with Docs Impact.

### Live-UAT checklist (PR-B) — operator-driven on `radio`

1. Force a `sipregisterinfo/get` 401 (or wait for natural PSIDTS expiry) → confirm `RotateCookies` now returns 200 with fresh `Set-Cookie` and the bridge re-registers WITHOUT falling through to CDP (log: "RotateCookies refreshed PSIDTS — reconnect backoff will retry" at `GVApiAdapter.cs:492`, and NO "run …refresh-from-browser" warning at `:503-505`).
2. `GET radio:5004/api/gvbridge/status` → `cookiesValid: true` and `psidtsAgeSeconds` resets toward 0 after rotation.
3. Place an outbound call immediately after a rotation → still connects (cookies still valid for `sipregisterinfo/get`).
4. CDP fallback still works when rotation genuinely can't help (e.g. dead `__Secure-1PSID`) → `refresh-from-browser` path unaffected.

### Risks (PR-B)

- **R-B1:** capture may reveal the endpoint requires a header we can't reproduce headless (e.g. a `SAPISIDHASH`/`Authorization`, an XSRF token fetched from a prior GET, or `X-Same-Domain`). If so, browser-less rotation may be infeasible and the CDP fallback stays primary — document the finding and DON'T ship a broken tightening. (This is exactly why it's a separate, capture-gated PR.)
- **R-B2:** Google may rotate MORE than PSIDTS (e.g. `__Secure-1PSIDCC`, `SIDCC`, `NID`). If the capture shows that, scope grows; flag before implementing.
- **R-B3:** fixture must be fully sanitized — no live cookies/tokens committed. Gate via review.

---

## OPEN QUESTIONS (for the coordinator to resolve with the user)

1. **(PR-A, design) Outbound state semantics.** OK to reuse `Dialing` as the outbound-ringing state (Approach A1, smallest diff, mirrors the BT path), or do you want an explicit `CallState.OutboundRinging` (Approach A2) so a UI can show "ringing"? Note: the active web UI lives in the RTest repo (per memory), so A2 would need a coordinated change there. **Recommend A1.**
2. **(PR-A, robustness) Outbound Dialing timeout.** Today inbound has a 60 s ringing timeout (`CallManager.cs:342-356`). With the deferral, an outbound call that never receives GV `Active` would sit in `Dialing` indefinitely until the user hangs up. Add a symmetric outbound-dialing timeout (recommend ~45 s → reset to Idle + teardown)? **Recommend yes.**
3. **(PR-B, docs) Missing research file.** The task and `GvCookieRotator.cs:21` cite `docs/research/gv-protocol-notes.md §3.2/§5.1`, but that file/dir does not exist; protocol research lives in `docs/api-research/`. Should the spike CREATE `docs/research/gv-protocol-notes.md`, or should we redirect the citations to `docs/api-research/`? **Recommend create the file at the cited path so the code comment becomes accurate.**
4. **(PR-B, security) Fixture commit policy.** Is a sanitized (secret-values redacted, header-names + synthetic PSIDTS values preserved) `RotateCookies` request/response fixture acceptable to commit to the repo for the replay test? Any values that must NOT be committed even redacted?
5. **(PR-B, ownership) Who runs the capture, and when?** The capture needs the live Google session on `radio` (CDP port 9224) and an operator — Planner/Builder cannot ssh or use the live account. Who performs B-Spike (B-S1/B-S2), and is the live account available now or should PR-B be queued as "blocked: awaiting capture"?
6. **(both) PR split confirmation.** Confirm TWO PRs (PR-A now, PR-B after capture) rather than one combined PR. **Recommend two.**

---

## Docs Impact (for the eventual planning/PR bodies)

- PR-A: `docs/KNOWN-ISSUES.md` (errno-101 note), `docs/SETUP-AND-TESTING.md` (outbound state-transition note).
- PR-B: `docs/research/gv-protocol-notes.md` (create §3.2/§5.1 from capture) OR redirect to `docs/api-research/`; `GvCookieRotator.cs` class comment (drop UNCONFIRMED TODO).
- This plan: `docs/plans/followup-incall-ordering-and-rotatecookies.md`.
