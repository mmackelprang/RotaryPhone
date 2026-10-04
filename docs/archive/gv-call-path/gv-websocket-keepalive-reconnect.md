> # ⚠️ SHIPPED — PR #36 (`ef9f2ba`), resolved 2026-06-13. Historical artifact. **Do NOT re-queue.**
>
> This plan's implementation **already merged** as PR #36 (`ef9f2ba Merge pull request #36 from
> mmackelprang/fix/gv-ws-keepalive-reconnect`; feature commits `a98f943`, `84a9a91`, `937e1c6`).
> `docs/KNOWN-ISSUES.md` records the defect *"Idle SIP WebSocket never reconnects → inbound calls stop
> ringing"* as **RESOLVED 2026-06-13**. Part C is in the tree verbatim: `GvSipTransport.IsRegistered`,
> `IsConnected`, `LastConnectedAt`, `GVApiAdapter.IsWebSocketConnected` / `SipLastConnectedAt`, and
> `GvBridgeStatusDto.WsConnected` / `LastConnectedAt`, plus the test
> `GetStatus_IncludesWsConnectedAndLastConnectedAt`.
>
> Only the **doc** was never committed — it sat uncommitted in the working tree for ~7 weeks while the
> code shipped, which is why the `Status: Ready for Builder` line below went stale. It is committed here
> (PR #71) **for the historical record**, per the B2 spec §3 and owner decision 2026-08-01 (§8 q4):
> retain rather than delete, so no future session re-queues shipped work.
>
> **§7 OPEN QUESTIONS 1-3 were all resolved in the shipped code**, and are recorded here so the section
> below is not read as still-open:
>
> 1. **Auth-failure escalation** — resolved as **(b), and further**. Automatic CDP refresh *is* wired, as
>    rung 3 of `GVApiAdapter.RecoverFromAuthFailureAsync` (rung 1 browser-less `RotateCookies` → rung 2
>    disk `ReloadCookiesAsync` → rung 3 `TryCdpRefreshAsync`). No manual-refresh log instruction was needed.
> 2. **Mid-call drop policy** — resolved as planned: reconnect signaling only; the active media /
>    `RTCPeerConnection` is left untouched and no SIP dialog resumption is attempted.
> 3. **Test timing seam** — resolved as **(b) + (c)**: `ReconnectOptions` for the schedule constants plus
>    an injectable `TimeProvider`. `[InternalsVisibleTo]` was already configured.
>
> Everything below this banner is the **original 2026-06 plan text, unedited**. Read it as history.

---

# Plan: GV SIP-over-WebSocket Keep-Alive + Auto-Reconnect + Honest Status

**Status:** Ready for Builder
**Scope:** PR1 only — WebSocket signaling-channel lifecycle (keep-alive, auto-reconnect/re-register, honest status).
**Out of scope:** The separate outbound-media / no-audio bug (tracked as PR2, under separate investigation). This plan touches signaling only — no RTP / DTLS / audio-path changes.
**Workflow:** Feature branch `fix/gv-ws-keepalive-reconnect` → PR targeting `main` → merge after UAT (per global branch-PR policy). RotaryPhone-internal change. **Not** a BT/audio-boundary change — the WebSocket to Google is unrelated to the Intel AX201 / TP-Link adapter boundary, so no `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` coordination is required.

---

## 1. Problem statement & confirmed root cause

**Symptom:** Inbound cell → rotary-phone calls (Google Voice, `GVApi` mode) intermittently never ring the phone.

**Confirmed root cause (diagnosed against live logs):**

- Google closes the GV SIP-over-WebSocket signaling socket after an idle period (~256s observed; Google advertises `keep=240` in the REGISTER 200-OK `Via`). The app sends no keep-alive, so Google drops the idle socket.
- When the socket drops, `GvSipWebSocketChannel.ReceiveLoopAsync` catches the close/error and **`break`s out of the loop — the loop simply ends with no reconnect and no notification**:
  - `src/RotaryPhoneController.GVBridge/Sip/GvSipWebSocketChannel.cs:96` — `break` on `WebSocketMessageType.Close`.
  - `src/RotaryPhoneController.GVBridge/Sip/GvSipWebSocketChannel.cs:132` — `break` on receive `Exception` (the exact line cited in the bug: `_logger.LogWarning(ex, "WebSocket receive error")` at line 130, then `break` at 132).
  - `src/RotaryPhoneController.GVBridge/Sip/GvSipWebSocketChannel.cs:123` — `break` on `OperationCanceledException`.
- While the loop is dead, inbound GV `INVITE`s never arrive, so `CallManager` never rings the HT801. (Proven: a freshly reconnected socket rang the phone and carried two-way audio.)
- `/api/gvbridge/status` reports `sipRegistered:true` even on a dead socket. The flag is `GvSipTransport._registered` (`src/RotaryPhoneController.GVBridge/Sip/GvSipTransport.cs:62`), set `true` on REGISTER 200-OK (`GvSipTransport.cs:783`) and **never reset when the socket dies** — surfaced via `GVApiAdapter.IsSipRegistered` (`GVApiAdapter.cs:49`) → `GVBridgeController.GetStatus` (`GVBridgeController.cs:49`).
- SIP credentials are long-lived (a live log showed `expires in 330883000s`), so re-REGISTER should **not** require re-extracting cookies in the common case — only on an auth failure.

**Why the existing `8395d66` fix does not cover this:** `8395d66` ("fix(gvbridge): check WebSocket health before outbound calls") made `EnsureRegisteredAsync` (`GvSipTransport.cs:97-106`) also check `_wsChannel.IsConnected` and force a re-register when dead. But `EnsureRegisteredAsync` is only invoked on an **outbound** call attempt (`InitiateAsync` → `GvSipTransport.cs:113`) and once at activation (`GVApiAdapter.cs:201`). Nothing calls it during an idle period, so an idle-then-dropped socket stays dead until the next outbound call or a manual `cookies/refresh-from-browser`. This plan **reuses** `EnsureRegisteredAsync`'s re-register logic as the single reconnect implementation and adds the missing triggers (push-on-close + periodic keep-alive) rather than building a parallel reconnect path.

---

## 2. Design

Three parts, all funneling through **one** reconnect entry point so there is no second parallel reconnect path.

### Part A — Keep-alive (stop Google from closing the idle socket)

1. **Parse `keep=` from the REGISTER 200-OK `Via`.** In the REGISTER-200 branch of the `MessageReceived` handler (`GvSipTransport.cs:780-794`, alongside the existing `Service-Route` extraction), extract the first `Via` header and look for a `keep=<n>` parameter (RFC 5626 §3.5.1 flow-keep-alive negotiation; Google echoes the client's `;keep` flag with a server-chosen interval, e.g. `keep=240`). Store it as `_keepAliveIntervalSeconds`. If absent or unparseable, default to `120` (the bug brief's "~120s if absent").
2. **Send a keep-alive at ~half the negotiated interval.** Schedule a periodic keep-alive every `max(15, keep/2)` seconds (so `keep=240` → 120s; clamp the floor to avoid pathological tiny values). Two mechanisms, applied as defense-in-depth:
   - **App-level double-CRLF ping (primary, RFC 5626 §3.5.1 / RFC 7118 §6).** Send the 4-byte `\r\n\r\n` (CRLF CRLF) ping over the WebSocket as a Text frame. The server is expected to answer with a 2-byte `\r\n` pong; we do not require the pong to consider the link healthy, but if we receive it we log it at debug. This is the keep-alive the SIP-over-WS spec defines and is most likely what `keep=` refers to.
   - **Protocol-level `ClientWebSocket.Options.KeepAliveInterval` (secondary).** Set this on the `ClientWebSocket` in `GvSipWebSocketChannel.ConnectAsync` (`GvSipWebSocketChannel.cs:43-58`) to roughly the negotiated interval. This sends WebSocket protocol PING frames at the transport layer and is independent of the SIP layer.
   - **Validation note for UAT:** we do not know which Google honors. The double-CRLF ping is the SIP-spec answer and is the primary; the `KeepAliveInterval` is cheap insurance. UAT (Section 5) must confirm the socket survives past the previously-observed ~256s drop with the ping running. If the double-CRLF ping turns out to be unnecessary (protocol ping alone suffices), we keep both — they are harmless and provide redundancy.
3. **The keep-alive timer lives in `GvSipTransport`**, mirrors the existing `Timer` pattern (`GVApiAdapter._healthCheckTimer` at `GVApiAdapter.cs:211`; `SipCallSession.SessionTimer` at `GvSipTransport.cs:1033`), is (re)started on every successful REGISTER, and is stopped/disposed on disconnect and in `DisposeAsync`.

### Part B — Auto-reconnect + re-register (push trigger + single-flight)

1. **Channel raises a `Closed` event instead of dying silently.** Add an `event EventHandler<WebSocketClosedEventArgs>? Closed;` to `GvSipWebSocketChannel`. At each `break` site in `ReceiveLoopAsync` (`:96`, `:123`, `:132`), record whether the exit was *intentional* (our own cancellation via `CloseAsync`) vs *unexpected* (server close / receive error). After the loop exits, raise `Closed` with that reason **only when unexpected** — i.e., not when `_receiveCts` was cancelled by our own `CloseAsync`/reconnect. (Raise outside the `while` so it fires exactly once per dead loop.)
2. **`GvSipTransport` subscribes to `Closed`** (in `RegisterAsync`, where the channel is created — `GvSipTransport.cs:747`) and, on an unexpected close, marks `_registered = false` and kicks the reconnect loop.
3. **Single-flight reconnect loop.** Add `ReconnectLoopAsync` to `GvSipTransport`:
   - Guarded by an `int _reconnecting` flag via `Interlocked.CompareExchange` so overlapping triggers (e.g. `Closed` event + keep-alive failure + an outbound call's `EnsureRegisteredAsync` all firing at once) collapse into a single in-flight reconnect. The flag is cleared in a `finally`.
   - Exponential backoff with jitter, capped: delays `1s, 2s, 4s, 8s, 16s, 30s (cap)`, plus ±20% random jitter, retrying **indefinitely** until reconnect succeeds or the transport is disposed/cancelled (Google may be briefly unreachable; we must not give up while the phone is expected to ring).
   - Each attempt calls the **existing** `RegisterAsync` path (the same code `EnsureRegisteredAsync` uses). On success, reset the backoff, restart the keep-alive timer, and (re)assert honest status.
4. **Re-register uses EXISTING credentials.** `RegisterAsync` already calls `_getCredentials()` (`GvSipTransport.cs:743`), which hits `sipregisterinfo/get`. Because SIP creds are long-lived, this normally succeeds without any cookie work. **Only escalate to a cookie refresh on an auth failure:** if REGISTER fails with `401`/`403` *after* the digest-auth retry (i.e. a genuine auth rejection, not the normal `401` challenge that `GvSipTransport.cs:795` already answers with Digest), surface that as an auth-failure signal so the adapter can trigger `ReloadCookiesAsync` / prompt a `cookies/refresh-from-browser`. **Do not** auto-refresh cookies on a plain network drop. See OPEN QUESTION 1 on exactly how to wire the escalation.
5. **Mid-call drop behavior.** If the socket drops while a call is active (`_activeCalls` non-empty), still reconnect (so future calls work and so an in-dialog `BYE`/`re-INVITE` can be sent if the call survives). But do **not** tear down the active `RTCPeerConnection` just because signaling dropped — DTLS-SRTP media flows peer-to-peer and can outlive a brief signaling gap. Log the mid-call drop prominently. (We do not attempt to "resume" the SIP dialog on the new socket — Google would need the dialog state re-established, which is out of scope; the realistic outcome is the current call's media continues until either side hangs up, and the *next* call benefits from the live socket.) See OPEN QUESTION 2.

### Part C — Honest status

1. **`GvSipTransport` exposes real channel state.** Add:
   - `public bool IsConnected => _wsChannel?.IsConnected ?? false;` (delegates to the existing `GvSipWebSocketChannel.IsConnected`).
   - `public DateTime? LastConnectedAt { get; private set; }` — set to `DateTime.UtcNow` on each successful REGISTER 200-OK.
   - Keep `IsRegistered` but make it honest: it should be `_registered && IsConnected` (so a dead socket can never report registered-true). Reset `_registered = false` on unexpected close (Part B.2).
2. **`GVApiAdapter` surfaces both.** Add `public bool IsWebSocketConnected => _sipTransport?.IsConnected ?? false;` and `public DateTime? SipLastConnectedAt => _sipTransport?.LastConnectedAt;`. Keep the existing `IsSipRegistered` (now backed by the honest `IsRegistered`).
3. **Status DTO + endpoint.** Update `GVBridgeController.GetStatus` (`GVBridgeController.cs:42-52`) to add `wsConnected` and `lastConnectedAt` alongside the existing `sipRegistered`. The status response is currently an anonymous object; introduce a typed `GvBridgeStatusDto` in `GvBridgeDtos.cs` for testability and so the contract is explicit. Existing fields (`available`, `activeMode`, `sipRegistered`, `cookiesValid`) are preserved exactly (the existing controller test `GetStatus_ReturnsAllFourFields` must still pass).

---

## 3. Testability seam (do this FIRST — Tasks 1-2)

`GvSipTransport` currently `new`s a concrete `GvSipWebSocketChannel` inside `RegisterAsync` (`GvSipTransport.cs:747`), which wraps a real `ClientWebSocket`. That makes keep-alive timing, reconnect-on-close, and status transitions impossible to unit-test without a live network. Introduce a seam:

1. **`ISipWebSocketChannel` interface** (new file `Sip/ISipWebSocketChannel.cs`) extracting the public surface of `GvSipWebSocketChannel`:
   ```
   Task ConnectAsync(CancellationToken ct = default);
   Task SendAsync(string sipMessage, CancellationToken ct = default);
   Task CloseAsync();
   bool IsConnected { get; }
   event EventHandler<SipMessageEventArgs>? MessageReceived;
   event EventHandler<WebSocketClosedEventArgs>? Closed;   // NEW (Part B.1)
   ```
   `GvSipWebSocketChannel : ISipWebSocketChannel, IDisposable`.
2. **Channel factory delegate** injected into `GvSipTransport`: `Func<Uri, ILogger, ISipWebSocketChannel>`. Default (production) factory `new GvSipWebSocketChannel(uri, logger)`. Tests inject a factory that returns a `Mock<ISipWebSocketChannel>` (Moq, already referenced) or a hand-rolled `FakeSipWebSocketChannel` that lets the test (a) raise `Closed`, (b) capture `SendAsync` payloads (to assert keep-alive `\r\n\r\n` was sent), (c) feed canned SIP responses via `MessageReceived` (REGISTER 200-OK with a `keep=` Via). Add the factory as an **optional** constructor parameter defaulting to the production factory so existing `new GvSipTransport(logger, getCredentials, loggerFactory)` call sites (`GVApiAdapter.cs:184`) keep compiling unchanged.

This seam is the single biggest enabler of the TDD plan below; everything in Section 4 assumes it exists.

---

## 4. Implementation tasks (bite-sized, ordered)

> Each task is independently committable. Follow TDD: write the failing test (or extend an existing one) before the implementation where a test is listed. Test project: `src/RotaryPhoneController.GVBridge.Tests` (xUnit + Moq).

### Task 1 — Add `WebSocketClosedEventArgs` + `Closed` event to the channel
- **File:** `src/RotaryPhoneController.GVBridge/Sip/GvSipWebSocketChannel.cs`
- Add `public sealed class WebSocketClosedEventArgs(bool wasIntentional, string? description) : EventArgs` (in this file or `SipModels.cs`).
- Add `public event EventHandler<WebSocketClosedEventArgs>? Closed;`.
- In `ReceiveLoopAsync`, track an `intentional` bool: set `true` in the `catch (OperationCanceledException) when (ct.IsCancellationRequested)` branch (`:121-124`); leave `false` for the server-`Close` (`:90-97`) and the generic-`Exception` (`:126-133`) branches.
- After the `while` loop exits (line ~134), raise `Closed?.Invoke(this, new WebSocketClosedEventArgs(intentional, _ws?.CloseStatusDescription))`. Guard so it fires once.
- **Test (`Sip/GvSipWebSocketChannelTests.cs`, new):** harder to test without a real socket; cover what is cheaply testable — `IsConnected` is `false` before connect; `Closed`/`WebSocketClosedEventArgs` shape compiles and a manually-invoked raise carries the expected `wasIntentional`. (Deep loop behavior is covered indirectly via the `GvSipTransport` fake-channel tests.)

### Task 2 — Extract `ISipWebSocketChannel` + inject a channel factory
- **Files:** new `src/RotaryPhoneController.GVBridge/Sip/ISipWebSocketChannel.cs`; edit `GvSipWebSocketChannel.cs` (implement interface); edit `GvSipTransport.cs`.
- Define `ISipWebSocketChannel` per Section 3.1. Make `GvSipWebSocketChannel` implement it.
- Add optional ctor param to `GvSipTransport`: `Func<Uri, ILogger, ISipWebSocketChannel>? channelFactory = null`, stored as `_channelFactory ??= (uri, log) => new GvSipWebSocketChannel(uri, log);`.
- In `RegisterAsync` (`GvSipTransport.cs:747`), replace `new GvSipWebSocketChannel(...)` with `_channelFactory(new Uri(WssUrl), _logger)`. Type the `_wsChannel` field as `ISipWebSocketChannel?`.
- **Before creating a new channel, dispose/close the old one and unsubscribe its handlers** (fixes the latent handler/channel leak — see Risks). Capture the `MessageReceived` and `Closed` handlers as named methods/locals so they can be `-=`'d, or dispose the old channel (whose handlers die with it).
- **Test (`Sip/GvSipTransportReconnectTests.cs`, new):** add a `FakeSipWebSocketChannel` implementing `ISipWebSocketChannel`; assert that injecting the factory lets a test construct `GvSipTransport` and drive a fake REGISTER handshake (feed a 200-OK via `MessageReceived`) to reach `IsRegistered == true`.

### Task 3 — Honest status fields on transport + adapter + DTO/endpoint
- **Files:** `GvSipTransport.cs`, `GVApiAdapter.cs`, `Api/GvBridgeDtos.cs`, `Api/GVBridgeController.cs`.
- `GvSipTransport`: add `IsConnected`, `LastConnectedAt` (set on REGISTER 200-OK at `:783`), make `IsRegistered => _registered && IsConnected`.
- `GVApiAdapter`: add `IsWebSocketConnected`, `SipLastConnectedAt`.
- `GvBridgeDtos.cs`: add `public record GvBridgeStatusDto(bool Available, string ActiveMode, bool SipRegistered, bool WsConnected, DateTime? LastConnectedAt, bool CookiesValid);`.
- `GVBridgeController.GetStatus`: return the DTO with `wsConnected = _adapter.IsWebSocketConnected` and `lastConnectedAt = _adapter.SipLastConnectedAt`, preserving the four existing field names.
- **Test (extend `Api/GVBridgeControllerTests.cs`):** new `GetStatus_IncludesWsConnectedAndLastConnectedAt` asserting `wsConnected` and `lastConnectedAt` properties exist; existing `GetStatus_ReturnsAllFourFields` / `GetStatus_DefaultValues_ShowUnavailable` still pass (defaults: `wsConnected=false`, `lastConnectedAt=null`).

### Task 4 — Parse `keep=` from REGISTER 200-OK Via
- **File:** `GvSipTransport.cs` (REGISTER-200 branch, ~`:780-794`).
- Add a private `static int ParseKeepInterval(string message)` helper: find the first `Via:` line, look for `;keep=` (or `keep=<n>`), parse the integer; return `0` if absent. Store result in `_keepAliveIntervalSeconds` (field), defaulting to `120` when parse yields `0`.
- **Test (`Sip/KeepAliveParsingTests.cs`, new):** table-driven `[Theory]` over sample `Via` strings — `;keep=240` → 240, `;keep` (flag only, no value) → default, missing → default, malformed `keep=abc` → default. Pure function, no I/O.

### Task 5 — Keep-alive timer (double-CRLF ping + `KeepAliveInterval`)
- **Files:** `GvSipTransport.cs`, `GvSipWebSocketChannel.cs`.
- `GvSipWebSocketChannel.ConnectAsync`: set `_ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(keepHint)` (pass the hint in via a ctor param or a property; default 120s). Add `Task SendPingAsync(CancellationToken ct)` that sends `"\r\n\r\n"` as a Text frame (reuse `SendAsync`). Expose on the interface.
- `GvSipTransport`: add `Timer? _keepAliveTimer`. Start/restart it after each successful REGISTER with period `Math.Max(15, _keepAliveIntervalSeconds / 2)` seconds; callback calls `_wsChannel.SendPingAsync`. On send failure, treat as a dropped link → trigger reconnect (Part B). Stop/dispose the timer on disconnect and in `DisposeAsync` (alongside the existing `_wsChannel` cleanup at `:1228-1232`).
- Recognize an inbound bare `\r\n` (pong) in `MessageReceived` and log at debug (do not route it through SIP parsing — it would currently hit none of the `StartsWith` branches and be silently ignored, which is acceptable, but an explicit early-return is cleaner).
- **Test (extend `Sip/GvSipTransportReconnectTests.cs`):** with the `FakeSipWebSocketChannel`, complete a fake REGISTER (feed 200-OK with `keep=4`), then assert that within a bounded wait the fake's captured sends include a `\r\n\r\n` ping. Use a small `keep` and a test-overridable timer interval (see OPEN QUESTION 3 on injecting a clock/short interval) so the test runs in well under a second rather than waiting real seconds.

### Task 6 — Single-flight reconnect loop wired to the `Closed` event
- **File:** `GvSipTransport.cs`.
- Add `int _reconnecting` and `private async Task ReconnectLoopAsync(CancellationToken ct)` with `Interlocked.CompareExchange(ref _reconnecting, 1, 0)` guard and `finally { Interlocked.Exchange(ref _reconnecting, 0); }`.
- Backoff schedule `1,2,4,8,16,30(cap)` seconds with ±20% jitter (`RandomNumberGenerator` or `Random.Shared`), looping until `RegisterAsync` succeeds or `ct` is cancelled.
- In `RegisterAsync`, subscribe `_wsChannel.Closed += OnChannelClosed;` where `OnChannelClosed`:
  - ignores intentional closes (`e.WasIntentional`),
  - sets `_registered = false`, stops the keep-alive timer,
  - starts `ReconnectLoopAsync` via `_ = ReconnectLoopAsync(_lifetimeCts.Token)` (fire-and-forget, single-flight-guarded).
- Add a `_lifetimeCts` (`CancellationTokenSource`) field cancelled in `DisposeAsync` so reconnect attempts stop on shutdown.
- Keep `EnsureRegisteredAsync` (`:97-106`) as a *pull* entry point that also funnels into the same `RegisterAsync` (it already does) — so an outbound call during a dead window still reconnects, and the single-flight guard prevents it from racing the push trigger.
- **Test (extend `Sip/GvSipTransportReconnectTests.cs`):**
  - `Reconnect_OnUnexpectedClose_ReRegisters`: complete a fake REGISTER, raise `Closed(wasIntentional:false)`, assert the transport attempts a new `ConnectAsync` + REGISTER (fake exposes a connect counter) and ends `IsRegistered == true` after feeding a fresh 200-OK.
  - `Reconnect_OnIntentionalClose_DoesNotReconnect`: raise `Closed(wasIntentional:true)`, assert no new connect attempt.
  - `Reconnect_IsSingleFlight`: raise `Closed` twice rapidly (and/or call `EnsureRegisteredAsync` concurrently), assert only one reconnect runs (connect counter increments once).
  - Use a backoff schedule that is test-overridable (inject the base delay, default 1s, set to e.g. 1ms in tests) — see OPEN QUESTION 3.

### Task 7 — Auth-failure escalation (cookie refresh only on real 401/403)
- **Files:** `GvSipTransport.cs`, `GVApiAdapter.cs`.
- In the REGISTER response handler, distinguish the **normal** challenge `401` (already answered with Digest at `:795-835`) from a **post-Digest** `401`/`403` (auth genuinely rejected). On the latter, set the REGISTER `regTcs` result to a failure that carries an `authFailure` reason (e.g. throw/return a typed result), and expose an event `event EventHandler? AuthenticationFailed;` on `GvSipTransport`.
- `GVApiAdapter` subscribes to `AuthenticationFailed` and, on fire, calls `ReloadCookiesAsync` (existing, `GVApiAdapter.cs:257`) and logs a clear "cookies likely expired — refresh via /api/gvbridge/cookies/refresh-from-browser" message. The reconnect loop continues its backoff; a successful `ReloadCookiesAsync` will let the next `RegisterAsync` attempt succeed.
- **Do not** trigger cookie refresh on network-level drops or on the normal `401` challenge.
- **Test (extend reconnect tests):** `Reconnect_PlainDrop_DoesNotRefreshCookies` (assert no `AuthenticationFailed`), and a unit test that feeds a post-Digest `401` and asserts `AuthenticationFailed` fires exactly once. (Adapter-level escalation wiring is verified by a `GVApiAdapter` test only if a seam exists; otherwise assert at the transport-event level and verify the adapter subscription by inspection.)

### Task 8 — Disposal / cancellation hardening
- **File:** `GvSipTransport.cs` `DisposeAsync` (`:1219-1235`).
- Cancel `_lifetimeCts`, stop+dispose `_keepAliveTimer`, ensure the in-flight reconnect loop observes cancellation and exits, then close+dispose `_wsChannel` (existing). Unsubscribe `Closed`/`MessageReceived` handlers.
- **Test:** `DisposeAsync_StopsKeepAliveAndReconnect` — after dispose, raising `Closed` on the (now-detached) fake does not start a new connect; no `ObjectDisposedException` escapes.

### Task 9 — Docs
- Update `docs/KNOWN-ISSUES.md`: move/annotate the "idle WebSocket never reconnects → inbound calls stop ringing" item to resolved, referencing this PR.
- Note the keep-alive + auto-reconnect behavior in `docs/SETUP-AND-TESTING.md` (how to observe reconnect in logs; what `wsConnected`/`lastConnectedAt` mean in `/api/gvbridge/status`).
- No boundary-doc change (not BT/audio).

---

## 5. Test plan (TDD + live UAT)

### Unit tests (xUnit + Moq, `src/RotaryPhoneController.GVBridge.Tests`)
The injected channel factory + `FakeSipWebSocketChannel` (Task 2) is the seam that makes all of this deterministic and network-free:

| Concern | Test | How |
|---|---|---|
| `keep=` parsing | `KeepAliveParsingTests` (Task 4) | Pure `[Theory]` over Via strings → expected interval/default. |
| Keep-alive sent | `KeepAlive_SendsDoubleCrlf` (Task 5) | Fake REGISTER with small `keep`; assert fake's captured sends contain `\r\n\r\n` within a bounded wait. |
| Reconnect on close | `Reconnect_OnUnexpectedClose_ReRegisters` (Task 6) | Raise `Closed(false)`; assert new connect + REGISTER; `IsRegistered` true after fresh 200-OK. |
| No reconnect on intentional close | `Reconnect_OnIntentionalClose_DoesNotReconnect` | Raise `Closed(true)`; assert connect count unchanged. |
| Single-flight | `Reconnect_IsSingleFlight` | Two rapid `Closed` raises / concurrent `EnsureRegisteredAsync`; assert one reconnect. |
| Re-register uses existing creds | covered by reconnect tests | `_getCredentials` fake returns canned creds; assert no cookie-refresh path invoked on plain drop. |
| Auth escalation | `AuthFailure_FiresEvent` / `PlainDrop_DoesNotRefreshCookies` (Task 7) | Feed post-Digest 401 → `AuthenticationFailed` fires; plain drop → it does not. |
| Honest status transitions | `Status_ReflectsConnectAndDrop` (Task 3/6) | After REGISTER: `IsConnected`/`IsRegistered` true, `LastConnectedAt` set; after `Closed(false)` before reconnect completes: `IsRegistered` false. |
| Status endpoint contract | `GetStatus_IncludesWsConnectedAndLastConnectedAt` (Task 3) | Controller test asserts new JSON fields; existing four still present. |
| Disposal | `DisposeAsync_StopsKeepAliveAndReconnect` (Task 8) | Post-dispose `Closed` raise is inert. |

Run: `dotnet test src/RotaryPhoneController.GVBridge.Tests`.

### Live UAT (rig at `radio:5004`)
Read-only GET endpoints are fine to poll; do **not** mutate the running service from this plan's authoring. The Builder/Tester performs UAT after deploy:

1. **Baseline:** `GET /api/gvbridge/status` → confirm `available:true`, `sipRegistered:true`, `wsConnected:true`, `lastConnectedAt` recent.
2. **Keep-alive survives idle:** leave the line idle past the previously-observed ~256s drop (e.g. 6+ minutes). Poll `/api/gvbridge/status` periodically. **Pass:** `wsConnected` stays `true` and `lastConnectedAt` does **not** change (socket never dropped — keep-alive worked). Confirm in logs that pings are being sent and no "WebSocket receive error" / re-register churn occurs.
3. **Forced-drop auto-reconnect:** induce a drop (e.g. block egress to `web.voice.telephony.goog` briefly, or kill the socket at the network layer — operator's choice; do not add a debug "kill socket" endpoint to production). **Pass:** logs show `Closed` → `ReconnectLoopAsync` backoff → new "SIP registration successful"; `/api/gvbridge/status` shows `wsConnected:false` during the gap then `true` with a **new** `lastConnectedAt`, all within **N ≤ 30s** of restored connectivity (backoff cap).
4. **Inbound call after recovery:** from a cell phone, call the GV number after a reconnect. **Pass:** the rotary phone rings and two-way audio works (the original repro).
5. **Idle-then-call (the core bug):** idle the line ~6 min, then place an inbound call **without** any manual `cookies/refresh-from-browser`. **Pass:** phone rings (proves keep-alive kept the socket alive OR auto-reconnect restored it).
6. **No reconnect storm:** over a long idle soak (30+ min) confirm logs show steady keep-alive pings, not repeated connect/REGISTER cycles.

---

## 6. Risks & edge cases

- **Reconnect storms.** Mitigated by single-flight (`Interlocked` guard) + capped exponential backoff + jitter. Without jitter, a Google-side outage affecting many reconnects would synchronize; jitter de-synchronizes. Cap at 30s so recovery is bounded.
- **Channel/handler leak (pre-existing, must fix in Task 2).** Today every `RegisterAsync` `new`s a fresh channel and `+=`-subscribes `MessageReceived` without disposing the old channel or unsubscribing — each reconnect would otherwise leak a channel and stack duplicate handlers (causing duplicate PRACK/ACK sends). Task 2 disposes the old channel before creating a new one.
- **Mid-call drop.** Reconnect proceeds but the active `RTCPeerConnection` is left alone (media is peer-to-peer DTLS-SRTP and survives a signaling gap). We do not resume the SIP dialog on the new socket. See OPEN QUESTION 2.
- **Auth-expiry escalation false positives.** Must distinguish the normal `401` challenge (answered with Digest at `:795`) from a real post-Digest `401`/`403`. Escalating on the normal challenge would spuriously demand a cookie refresh. Task 7 only escalates post-Digest.
- **Thread-safety / cancellation / disposal.** Keep-alive `Timer` callbacks, the reconnect loop, the receive loop, and `DisposeAsync` all touch `_wsChannel` / `_registered`. Use the `Interlocked` single-flight guard, a `_lifetimeCts` cancelled on dispose, and ensure the reconnect loop and keep-alive timer observe cancellation. Avoid `async void`; use fire-and-forget `Task` with a logged `ContinueWith`/try-catch so an exception in the reconnect loop can't crash the process.
- **`keep=` semantics uncertainty.** Google may use `keep=` to mean "client must ping within N seconds" or "server will ping every N." We ping at half the interval (conservative) and also set the protocol `KeepAliveInterval`. UAT step 2 validates empirically; both mechanisms are harmless if redundant.
- **Double-CRLF routed as SIP.** The current `MessageReceived` dispatch only matches `SIP/2.0` / `BYE ` / `INVITE ` prefixes, so a stray `\r\n` pong is already harmlessly ignored — but add an explicit early return for clarity (Task 5).
- **Existing test contract.** The status endpoint already has tests asserting the four fields; adding fields must not rename/remove them.

---

## 7. OPEN QUESTIONS (for the coordinator to resolve with the user)

1. **Auth-failure escalation wiring.** On a genuine post-Digest `401`/`403`, should `GvSipTransport` (a) fire an `AuthenticationFailed` event that `GVApiAdapter` handles by calling `ReloadCookiesAsync()` (reads cookies already on disk — only helps if a fresh set was imported out-of-band), or (b) additionally auto-invoke the CDP `cookies/refresh-from-browser` flow (requires a Chrome instance with remote debugging on the host — may not be running on `radio` headless)? Plan currently assumes (a) plus a clear log instruction to run the manual refresh. Confirm whether automatic CDP refresh is desired/feasible on the live box.

2. **Mid-call drop policy.** Plan assumes: reconnect signaling but leave the active media/`RTCPeerConnection` untouched and do not attempt SIP dialog resumption. Acceptable, or should a mid-call signaling drop instead actively tear down the call (send local hangup to the HT801 so the user isn't left on a dead call)? This affects `CallManager`/`GVApiAdapter` hangup wiring and could widen scope slightly.

3. **Test timing seam.** To keep keep-alive and backoff tests fast/deterministic, the plan needs the keep-alive interval and backoff base-delay to be test-overridable (e.g. inject via an internal constructor param or an `internal` options object, defaulting to production values). Preferred mechanism: (a) `internal` ctor overload with `InternalsVisibleTo` the test project, (b) an `internal sealed class ReconnectOptions { TimeSpan BaseBackoff; ... }` injected, or (c) an injectable `TimeProvider` (`.NET 8+`)? Recommendation: option (c) `TimeProvider` for the timer/delays where practical, falling back to (b) for the schedule constants. Confirm preference. (Check whether `[InternalsVisibleTo]` is already configured for this test project; if not, Task adds it.)

---

## 8. Reconciliation summary with `8395d66`

- **Reused:** `EnsureRegisteredAsync`'s "if not connected, force re-register" check (`GvSipTransport.cs:97-106`) and `GvSipWebSocketChannel.IsConnected` (added by `8395d66`) remain the foundation. The reconnect *implementation* stays `RegisterAsync`.
- **Added (the gap `8395d66` left):** a **push** trigger (`Closed` event) so an idle socket reconnects without waiting for an outbound call; a **periodic keep-alive** so the socket rarely drops at all; **single-flight + backoff** so the new automatic triggers don't storm; and **honest status** so `sipRegistered`/`wsConnected` reflect reality.
- **One path, not two:** all triggers (push `Closed`, periodic keep-alive failure, pull `EnsureRegisteredAsync` on outbound) funnel through the single-flight `ReconnectLoopAsync` → `RegisterAsync`. No parallel reconnect path is introduced.
