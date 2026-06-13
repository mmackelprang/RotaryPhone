# Google Voice Web Client: SIP-over-WebSocket Protocol Notes

**Purpose:** Reverse-engineering reference for the GV web calling protocol
(`voice.google.com` / `wss://web.voice.telephony.goog/websocket`), focused on what
PR1 needs: keep-alive, auto-reconnect, honest status, and 401-on-re-register recovery.

**Method:** Primary source is this repo's existing reverse-engineering
(`src/RotaryPhoneController.GVBridge/**`). Secondary source is public RFCs
(5626 / 6223 / 7118 / 6455) and public reverse-engineering of Google's
SAPISIDHASH + rotating-cookie auth (the same scheme Gemini / Bard web clients use).
Last updated 2026-06-13.

> TL;DR for PR1: There is already a detailed, ready-for-Builder plan at
> `docs/plans/gv-websocket-keepalive-reconnect.md`. This research **confirms** its
> keep-alive design and **sharpens** its auth-recovery design. The two changes worth
> making before Builder starts are in the "Summary / impact on the PR1 plan" section
> at the bottom.

---

## 1. What the repo code already implements (and the gaps)

### 1.1 WebSocket channel — `Sip/GvSipWebSocketChannel.cs`

Implemented:
- `ClientWebSocket` with sub-protocol `sip` (RFC 7118 §4.1 requires
  `Sec-WebSocket-Protocol: sip`), `Origin: https://voice.google.com`, and a
  Chrome `User-Agent` (`GvSipWebSocketChannel.cs:43-47`). All correct per RFC 7118.
- Receive loop accumulates frames and raises `MessageReceived` per complete message.
- Connect timeout 10s.

Gaps (these are the PR1 bug):
- **No keep-alive.** `ClientWebSocket.Options.KeepAliveInterval` is never set, and
  no app-level CRLF ping is sent. The socket goes idle and Google drops it (~256s
  observed; Google advertises `keep=240` — see §2).
- **No reconnect / no notification on close.** Every exit path in `ReceiveLoopAsync`
  just `break`s and the loop ends silently:
  - `GvSipWebSocketChannel.cs:96` — `break` on server `Close`.
  - `GvSipWebSocketChannel.cs:123` — `break` on `OperationCanceledException`.
  - `GvSipWebSocketChannel.cs:132` — `break` on generic receive `Exception`
    (the exact "remote party closed the WebSocket connection" path).
  There is no `Closed` event, so `GvSipTransport` is never told the socket died.

### 1.2 SIP transport — `Sip/GvSipTransport.cs`

Implemented (and accurate to captured traffic):
- **REGISTER** (`BuildRegister`, `:1132`): `REGISTER sip:web.c.pbx.voice.sip.google.com`,
  `Via: SIP/2.0/wss <random>.invalid;branch=...;keep` (note the bare `;keep` flag —
  RFC 6223 keep-alive *request*), `Supported: path,gruu,outbound,record-aware`
  (RFC 5626 outbound), `Contact: ...;+sip.ice;reg-id=1;+sip.instance="<urn:uuid:...>";expires=3600`
  (RFC 5626 reg-id/instance), `X-Google-Client-Info` (base64 protobuf client id),
  `Expires: 3600`.
- **Digest auth on 401 challenge** (`:795-835`): MD5 Digest where
  `username = creds.SipUsername` (the SIP identity token from `sipregisterinfo/get`)
  and `password = creds.BearerToken` (the crypto key from the same response).
  HA1 = MD5(user:realm:pass), HA2 = MD5(REGISTER:sip:domain), response = MD5(HA1:nonce:HA2).
  This is the normal challenge → re-send flow; it is **not** an auth failure.
- **REGISTER 200 OK handling** (`:780-794`): sets `_registered = true`, extracts
  `Service-Route` for INVITE routing. **Does not parse `keep=` from the response Via.**
- INVITE / 183 / PRACK / 200 / ACK flow, incoming INVITE → 180/200, BYE handling,
  session-timer re-INVITE. (Call-signaling detail in §4.)
- `EnsureRegisteredAsync` (`:97-106`): if `!_registered || !_wsChannel.IsConnected`,
  forces a full `RegisterAsync`. Added by commit `8395d66`.

Gaps:
- **`EnsureRegisteredAsync` is pull-only.** It is called from `InitiateAsync`
  (outbound, `:113`) and once at activation (`GVApiAdapter.cs:201`). **Nothing calls it
  during idle**, so an idle-dropped socket stays dead until the next outbound call or a
  manual cookie refresh — and inbound INVITEs silently never arrive. This is the core bug.
- **`_registered` is never reset on socket death** (`:62` set true at `:783`,
  never set false). So `/api/gvbridge/status` reports `sipRegistered:true` on a dead socket.
- **`keep=` from the 200-OK Via is ignored** — no keep-alive interval is derived.
- **Latent handler/channel leak:** `RegisterAsync` `new`s a fresh `GvSipWebSocketChannel`
  every call (`:747`) and `+=` subscribes `MessageReceived` (`:765`) without disposing the
  old channel or unsubscribing — a reconnect path would stack duplicate handlers
  (duplicate PRACK/ACK). The PR1 plan already calls this out (Task 2 / Risks).

### 1.3 SIP credentials — `Sip/GvSipCredentialProvider.cs`

- Calls `POST voice/v1/voiceclient/sipregisterinfo/get?alt=protojson&key=<API_KEY>`
  with body `[3,"gvresearch-<machine>"]`, content-type `application/json+protobuf`.
- Response shape `[[ts, expiryMs], null, null, ["sipIdentityToken","cryptoKey"]]`.
  `SipUsername = position[3][0]`, `BearerToken (Digest password) = position[3][1]`,
  expiry = `position[0][1]`. A live log showed `expires in 330883000s` — effectively
  permanent. **So re-REGISTER does NOT normally need new cookies; only the
  `sipregisterinfo/get` HTTP call itself can fail auth.**
- Auth on this HTTP call is **not** in this file — it is added by the
  `GvHttpClientHandler` (§1.4) on every request from the shared `HttpClient`.

### 1.4 HTTP auth — `Auth/GvHttpClientHandler.cs`, `Auth/GvSapisidHash.cs`, `Auth/GvCookieSet.cs`

- Every GV HTTP request (`account/get` health check, `sipregisterinfo/get`,
  `threadinginfo/get`) gets:
  - `Authorization: SAPISIDHASH <ts>_<sha1(ts + " " + SAPISID + " " + origin)>`
    (`GvSapisidHash.Compute`, `GvHttpClientHandler.cs:25-27`). Origin is
    `https://voice.google.com`. **Single-variant** SAPISIDHASH only.
  - `Cookie:` = `GvCookieSet.ToCookieHeader()` which, when `RawCookieHeader` is present
    (it always is after a CDP/browser capture), returns the **verbatim captured cookie
    string** (`GvCookieSet.cs:24-28`).
  - `Origin`, `Referer: https://voice.google.com/`, `X-Goog-AuthUser: 0`.
- `GvCookieSet` has typed fields only for the **long-lived** cookies
  (SAPISID/SID/HSID/SSID/APISID + `__Secure-1PSID`/`__Secure-3PSID`). The **rotating**
  freshness cookies (`__Secure-1PSIDTS`/`__Secure-3PSIDTS`, and `*PSIDCC`) ride along
  **only inside `RawCookieHeader`**, captured once and never updated (confirmed: no
  PSIDTS/RotateCookies logic anywhere in the codebase — grep found only field plumbing).

> **This is the root of the `sipregisterinfo/get` 401.** See §3.

### 1.5 Cookie lifecycle — `Services/GvCookieManager.cs`, `Adapters/GVApiAdapter.cs`, `Auth/CookieRetriever.cs`, `Api/GVBridgeController.cs`

- Cookies stored AES-256-GCM encrypted on disk (`TokenEncryption.cs`).
- **Validity "check" is shallow:** `GvCookieManager.GetStatus()` (`:41-79`) reports
  `cookiesValid = adapter.AreCookiesValid`, and `AreCookiesValid` is just the last
  `account/get` health-check result (`GVApiAdapter.RunHealthCheckAsync`, `:405`).
  There is **no per-cookie freshness/expiry inspection** of `__Secure-1PSIDTS`, which is
  why a "superficial cookie-validity check still said valid" while the live request 401'd —
  the health check ran earlier, before the PSIDTS rotated, or the timer hadn't fired.
- **Two refresh paths, both full re-extraction:**
  - `CookieRetriever.RetrieveAndSaveAsync` (CDP via Playwright, port 9222) — launches/uses
    Chrome, dumps all cookies, encrypts to disk, verifies with `account/get`.
  - `GVBridgeController.RefreshCookiesFromBrowser` (`cookies/refresh-from-browser`, CDP via
    raw WebSocket `Network.getCookies`, port `_config.ChromeCdpPort` e.g. 9224) — the path
    used in live debugging to fix the 401.
- `ReloadCookiesAsync` (`GVApiAdapter.cs:257`) re-reads cookies **already on disk** and
  re-runs the health check; it does **not** fetch anything new. Useful only after an
  out-of-band refresh has updated the file.
- **Nothing automatically refreshes the rotating cookie.** Recovery today = a human runs
  `cookies/refresh-from-browser` against a logged-in Chrome.

---

## 2. Keep-alive recommendation for PR1

### 2.1 What `keep=240` actually is (RFC clarification — important)

The repo plan attributes `keep=` to "RFC 5626 §3.5.1". That is slightly off and worth
correcting in the plan:

- The **`keep` Via parameter is defined in RFC 6223** ("Indication of Support for
  Keep-Alive"), not RFC 5626. The client puts a bare `;keep` flag on its topmost Via
  (the repo already does this — `BuildRegister` and every request use `;keep`). The server
  echoes `;keep=NNN` in the Via of its response, where **NNN is a *recommended keep-alive
  frequency in seconds*** that the client should send keep-alives at.
  RFC 6223 verbatim: *"The parameter value, if present and with a value other than zero,
  represents a recommended keep-alive frequency, given in seconds."* and *"the SIP entity
  must send keep-alives at least as often as the indicated recommended keep-alive frequency,
  and if the SIP entity uses the recommended keep-alive frequency, then it should send its
  keep-alives so that the interval between each keep-alive is randomly distributed between
  80% and 100% of the recommended keep-alive frequency."*
- So `keep=240` means **"send a keep-alive roughly every 192-240s"** — NOT "the server
  pings you" and NOT "the socket dies at exactly 240s". The observed ~256s idle drop is
  Google's flow-dead timeout (a bit of slack past 240).
- The **keep-alive *method*** for a connection-oriented transport (WebSocket counts) is
  the **RFC 5626 §3.5.1 CRLF technique**, which RFC 7118 §6 explicitly says is usable over
  SIP-over-WebSocket. RFC 6223 selects *when*; RFC 5626 defines *what*.

CRLF technique byte sequences (RFC 5626 §3.5.1, confirmed):
- **Client ping = double-CRLF = `CR LF CR LF` = `0x0D 0x0A 0x0D 0x0A`** (4 bytes).
- **Server pong = single CRLF = `CR LF` = `0x0D 0x0A`** (2 bytes).

RFC 7118 §6 also notes WebSocket-native Ping frames (RFC 6455 §5.5.2) are allowed, but
warns *"The WebSocket API does not provide a mechanism for applications running in a web
browser to control whether or not periodic WebSocket Ping frames are sent"* — i.e. the
**browser GV client cannot use WS Ping frames and therefore almost certainly relies on the
CRLF double-CRLF ping.** That strongly implies **the double-CRLF is the keep-alive Google
honors**, and is the one PR1 must send. (We are not a browser, so we *can also* set
`ClientWebSocket.Options.KeepAliveInterval` as cheap insurance, but do not rely on it alone.)

### 2.2 Recommendation (confirms the existing plan, with the RFC fix)

1. **Parse `keep=` from the REGISTER 200-OK first Via.** Store `_keepAliveIntervalSeconds`.
   Default to `120` if absent/unparseable. (Plan Task 4 — correct; just fix the RFC
   citation to RFC 6223 for the `keep` param, RFC 5626 §3.5.1 for the CRLF method.)
2. **Primary keep-alive = app-level double-CRLF ping** (`"\r\n\r\n"` as a WebSocket *Text*
   frame, matching how the channel already sends SIP). Send every
   `max(15, keep/2)` s (so `keep=240` → 120s). Sending at *half* the negotiated value is
   safely inside RFC 6223's 80-100% guidance and gives margin against the ~256s drop.
   - Optionally add ±10-20% jitter per RFC 6223, but half-interval is already conservative;
     jitter matters more for the reconnect backoff (§ below) than for a single client.
   - The server's single-CRLF pong will arrive as an inbound message that matches none of
     the `SIP/2.0`/`BYE `/`INVITE ` dispatch prefixes in `GvSipTransport` and is harmlessly
     ignored today; add an explicit early-return for a bare `\r\n` and log at debug.
3. **Secondary = `ClientWebSocket.Options.KeepAliveInterval`** set to ~`keep` seconds in
   `ConnectAsync`. Defense-in-depth; harmless if redundant.
4. **Timer lives in `GvSipTransport`**, (re)started on every successful REGISTER, stopped on
   disconnect and in `DisposeAsync`. Mirror the existing `Timer` usage
   (`SipCallSession.SessionTimer` `:1033`, `GVApiAdapter._healthCheckTimer` `:211`).
5. **On ping send failure → treat as a dead link → trigger reconnect** (§ below). A failed
   send is the earliest, cheapest drop signal we get.

UAT to confirm (already in the plan §5 step 2): idle 6+ min, `wsConnected` stays true,
`lastConnectedAt` unchanged, logs show pings, no receive-error churn.

---

## 3. Auth / 401-recovery recommendation for PR1

### 3.1 Why `sipregisterinfo/get` returns 401 after a while (root cause)

`sipregisterinfo/get` is authenticated by `GvHttpClientHandler` with **(a)** a SAPISIDHASH
derived from the long-lived `SAPISID` cookie and **(b)** the full `Cookie:` header from the
stored `RawCookieHeader`. The 401 is **not** a SAPISIDHASH problem (SAPISID and the SHA1 are
recomputed fresh per request from the current timestamp, so they never "age"). The 401 is a
**stale rotating-cookie** problem:

- Google rotates **`__Secure-1PSIDTS` / `__Secure-3PSIDTS`** (the "timestamp/freshness"
  partners of `__Secure-1PSID`/`__Secure-3PSID`) on its **own server-side cadence** (minutes
  to a few hours). The on-disk `Expires` is **not** a reliable predictor of server-side
  validity — Google can invalidate the old PSIDTS before its cookie expiry.
- The repo captures the cookie header **once** and replays it verbatim forever
  (`GvCookieSet.RawCookieHeader`). It **never** refreshes PSIDTS. Once Google rotates,
  the stored PSIDTS is stale → backend returns **401 `SESSION_COOKIE_INVALID`**. (The
  signaler doc already records: *"PSIDTS cookies are required — without them, all requests
  return 401 SESSION_COOKIE_INVALID."*)
- The "superficial cookie check said valid" because `AreCookiesValid` is just the last
  `account/get` health-check result on a timer, plus the presence of `SAPISID` — neither
  inspects PSIDTS freshness.

This is the **same** failure mode and the **same** fix that the Gemini/Bard web-API
community libraries (e.g. `gemini-webapi`, `HanaokaYuzu/Gemini-API`) solved — those tools
run "always-on" against the identical Google cookie scheme.

### 3.2 Can we recover WITHOUT a full CDP re-extraction? — **Yes, very likely.**

There is a documented, browser-less programmatic refresh of the rotating cookie:

- **`POST https://accounts.google.com/RotateCookies`**
  - Request body: a small JSON array `[og_pid, init_value]` (the public reference uses
    `[1, ""]` / values scraped from a prior `RotateCookiesPage` request; a best-effort
    `[0, "..."]`-style "poke" is what the always-on libraries send).
  - Required `Cookie:` header: at minimum the long-lived **`__Secure-1PSID`** plus the
    current **`__Secure-1PSIDTS`** (and the 3P equivalents). These long-lived cookies are
    the ones the repo *does* store as typed fields.
  - Response: `Set-Cookie: __Secure-1PSIDTS=<fresh>` (and `__Secure-3PSIDTS`). Parse the new
    value out and splice it back into the stored cookie set.
  - `gemini-webapi` does exactly this on a background interval ("Automatically refreshes
    cookies in background. Optimized for always-on services… will automatically refresh
    `__Secure-1PSIDTS` in the background as long as the process is alive"), needing only
    `__Secure-1PSID` (+ optional `__Secure-1PSIDTS`).

**Implication for PR1's 401 escalation:** The plan's Open Question 1 asks whether escalation
should be (a) `ReloadCookiesAsync` + a "go run the manual refresh" log, or (b) auto-invoke
the CDP `cookies/refresh-from-browser`. There is a **better third option (c)** that needs
neither a human nor a running Chrome on the headless `radio` box:

> **(c) A lightweight, browser-less `RotateCookies` refresh** that mints a fresh
> `__Secure-1PSIDTS`/`__Secure-3PSIDTS` from the stored long-lived `__Secure-1PSID`/`3PSID`,
> updates the in-memory + on-disk cookie set, and retries `sipregisterinfo/get` once.

This is the right primary recovery for the live box (Linux, no desktop Chrome). The CDP
`refresh-from-browser` flow remains the fallback for the rare case where the long-lived
`__Secure-1PSID` itself has been revoked (full re-login required — that genuinely needs a
browser).

Caveats / things to verify when implementing (needs a packet capture or a live test):
- The exact `RotateCookies` body and any `Referer`/`Origin` requirements may have drifted;
  the public references are from the Bard/Gemini origin, not `voice.google.com`. Confirm the
  request works with `origin=https://voice.google.com` (or whatever the bundle uses) against
  the live account before committing to it as primary. If it proves finicky, ship the
  *escalation event* now (plan Task 7) and wire `RotateCookies` as the handler in a fast
  follow.
- After a successful rotate, the new PSIDTS must be merged back into **both** the typed
  fields *and* the `RawCookieHeader` (or `ToCookieHeader()` changed to overlay refreshed
  PSIDTS onto the raw header) — otherwise the verbatim raw header keeps sending the stale one.
  This is a real code change to `GvCookieSet`/`ToCookieHeader`, not just an HTTP call.

### 3.3 Distinguish "normal challenge" from "real auth failure"

- The SIP **`401` on REGISTER** (`GvSipTransport.cs:795`) is the **normal Digest challenge** —
  always answered with the Digest re-send. **Do NOT** treat this as an auth failure / do NOT
  trigger any cookie refresh on it. (Plan already handles this correctly.)
- The **`401`/`403` on the HTTP `sipregisterinfo/get`** (or on `account/get`) **is** the real
  auth failure → trigger the §3.2 cookie refresh, then retry. This HTTP 401 surfaces in
  `GvSipCredentialProvider.GetCredentialsAsync` (`response.EnsureSuccessStatusCode()` throws,
  `:62`) — that is the natural place to detect it and signal escalation.
- A post-Digest SIP `401`/`403` (auth genuinely rejected after the Digest re-send) is also a
  real failure but a different one (bad SIP token) — usually fixed by re-fetching
  `sipregisterinfo/get`, which itself may need the cookie refresh first.

### 3.4 Recommended escalation ladder for PR1

1. Plain network drop / WS close → reconnect with backoff, **no cookie work**.
2. `sipregisterinfo/get` returns **401/403** → attempt **`RotateCookies` refresh (3.2c)** →
   re-run `sipregisterinfo/get` once → continue REGISTER. (If 3.2c isn't shipped in PR1,
   fire `AuthenticationFailed` → `ReloadCookiesAsync` + a clear log telling the operator to
   run `cookies/refresh-from-browser`.)
3. `RotateCookies` itself 401s, or refresh+retry still 401s → the long-lived session is dead;
   fall back to the CDP `cookies/refresh-from-browser` flow / operator re-login.
4. Also add a **real** cookie-freshness signal to status: surface PSIDTS age / last-rotate so
   `/api/gvbridge/status`'s `cookiesValid` isn't purely the stale periodic health check.

---

## 4. Call-signaling notes (lower priority for PR1 — context for PR2)

### 4.1 Outbound (INVITE we send) — `GvSipTransport.InitiateAsync` / REGISTER handler

`INVITE sip:+1NNN@web.c.pbx.voice.sip.google.com` with:
- `Via: SIP/2.0/wss <regWsHost>;branch=...;keep`, `From: <sip:<sipUsername>@domain>;tag=...`,
  `Contact: <sip:<regContactUser>@<regWsHost>;transport=wss>`.
- `Supported: timer,100rel,ice,replaces,outbound,record-aware`, `Session-Expires: 90`,
  `X-Google-Client-Info`, `Route: <Service-Route from REGISTER>` if present.
- SDP from a `SIPSorcery RTCPeerConnection` (DTLS-SRTP, Opus 48k stereo PT 111 + G722/PCMU/PCMA),
  `X_UseRsaForDtlsCertificate = true`, `X_UseRtpFeedbackProfile = true` (SAVPF, matches Chrome).
- Response handling: `183`/`180` → set remote SDP (from 183), then **PRACK** (100rel);
  `200` → **ACK** + store dialog (Contact, To/From, reversed Record-Route route set) +
  start session-timer re-INVITE at `Session-Expires/2`.

### 4.2 Inbound (INVITE Google sends us) — `HandleIncomingInvite`

Dispatched when an inbound WS message `StartsWith("INVITE ")` (`:1096`). Sends `180 Ringing`
(echoing all `Via`s, a generated dialog `tag`), builds an answer `RTCPeerConnection` from the
INVITE SDP, sends `200 OK` with the SDP answer (same dialog tag), fires `IncomingCallReceived`.
For our outbound BYE it swaps From/To so we are `From` (callee + our tag) and the caller is `To`.

> **This inbound INVITE never arriving when the socket is dead is the whole PR1 bug** — the
> receive loop is gone, so Google's INVITE lands on a closed socket and the phone never rings.

### 4.3 Non-standard / Google-specific bits
- **`;keep` on every Via** — RFC 6223 keep-alive support indication (see §2).
- **`+sip.ice;reg-id=1;+sip.instance="<urn:uuid:...>"` in Contact** + `Supported: outbound`
  — RFC 5626 outbound registration (single flow, reg-id 1).
- **`X-Google-Client-Info`** — base64 protobuf identifying `GoogleVoice voice.web-frontend_…`
  + `Chrome 146`. Sent on REGISTER, PRACK, INVITE.
- **PRACK route ordering**: Route headers are **reversed** from Record-Route, "non-econt first"
  per the inline comment (`:907-921`). The `uri-econt` reference in the bug brief refers to one
  of the two Record-Route entries Google inserts (an "edge continuation"/edge-proxy URI); the
  working PRACK/ACK reverse the Record-Route set and the code notes the non-econt entry must
  lead. No change needed for PR1; flagged for the PR2 outbound-audio investigation since route
  ordering and the SDP/DTLS direction are the likely suspects there.
- **Google ignores our BYE** (`docs/KNOWN-ISSUES.md`) — worked around by closing DTLS first.
  Out of scope for PR1.

The separate **outbound one-way-audio** bug is media-path (DTLS-SRTP / SDP / RTP), not
signaling — explicitly PR2, out of scope here. Nothing in this research changes that split.

---

## 5. Open items needing the raw JS bundle or a packet capture

1. **Exact `RotateCookies` request shape for `voice.google.com`.** The body
   `[og_pid, init_value]`, the `Referer`/`Origin`, and whether a `key=` is needed are from the
   Bard/Gemini origin. A capture of the GV web client's background `accounts.google.com/RotateCookies`
   call (or trial against the live account) would confirm the exact request before making 3.2c
   the *primary* recovery. Without it, ship the escalation event/seam now and confirm the rotate
   request in a fast follow.
2. **Whether Google honors the WS-native Ping** in addition to the double-CRLF. RFC 7118 implies
   the browser client can't send WS Pings, so the CRLF is almost certainly the real keep-alive,
   but a capture of the live GV client's idle traffic would confirm exactly what it sends and at
   what cadence (does it ping at `keep`, `keep/2`, or with jitter?). PR1's "send at keep/2 +
   protocol KeepAliveInterval" is safe regardless.
3. **The signaler long-poll channel** (`signaler-pa.clients6.google.com`, `chooseServer`/bind/poll
   in `docs/api-research/signaler-protocol.md`) is a *separate* push channel from the SIP WSS and
   uses the **same** rotating cookies — so the §3.2 PSIDTS refresh helps it too. Out of scope for
   PR1 but worth noting the shared auth.
4. **`keep=` value stability.** Confirm Google always sends `keep=240` (vs varying). PR1 parses it
   dynamically with a 120 default, so a change is handled, but documenting the observed value helps UAT.

---

## Summary / impact on the PR1 plan

The existing plan (`docs/plans/gv-websocket-keepalive-reconnect.md`) is sound and this research
**confirms** its keep-alive + reconnect + honest-status design. Two things should change/confirm
before Builder starts:

1. **Fix the RFC citation and lock in the double-CRLF as the primary keep-alive.** `keep=` is the
   **RFC 6223** Via parameter (a *recommended send frequency in seconds*, here 240), and the
   keep-alive *method* is the **RFC 5626 §3.5.1 CRLF** technique (client double-CRLF `\r\n\r\n`,
   server single-CRLF pong), which RFC 7118 §6 permits over WebSocket. Because a browser can't
   drive WS-native Pings (RFC 7118 §6), the double-CRLF is almost certainly what Google honors, so
   it must be the primary (send at `keep/2` ≈ 120s); the `ClientWebSocket.KeepAliveInterval` stays
   as secondary insurance. (Plan Task 4/5 — just correct the citation and the primacy note.)

2. **Upgrade the 401 recovery from "ReloadCookies + ask a human / CDP" to a browser-less
   `accounts.google.com/RotateCookies` refresh of the rotating `__Secure-1PSIDTS`/`3PSIDTS`.**
   The 401 is a **stale rotating-cookie** problem, not a SAPISIDHASH problem — Google rotates
   PSIDTS server-side and the repo replays a captured cookie header verbatim forever. The fix
   used by every always-on Google-cookie library is a lightweight `RotateCookies` POST that mints
   a fresh PSIDTS from the stored long-lived `__Secure-1PSID`. This is feasible on the headless
   `radio` box (no Chrome needed) and should be PR1's **primary** auth recovery, with the existing
   CDP `cookies/refresh-from-browser` kept as the fallback for a truly dead login. Implementing it
   requires `GvCookieSet`/`ToCookieHeader` to overlay the refreshed PSIDTS onto the stored
   `RawCookieHeader` (the rotating cookies currently live only in that verbatim string and are
   never updated). This resolves the plan's **Open Question 1** with a concrete, browser-less answer.

3. (Confirm) **Make `cookiesValid` honest too.** Today it's just the periodic `account/get` result,
   which is why the "superficial check said valid" while live requests 401'd. Surfacing PSIDTS
   age / last-rotate alongside the keep-alive/reconnect status fields would make `/api/gvbridge/status`
   reflect the real failure mode this research identified.

**Doc path:** `docs/research/gv-protocol-notes.md`

Sources: [RFC 7118](https://www.rfc-editor.org/rfc/rfc7118.html),
[RFC 5626 §3.5.1](https://www.rfc-editor.org/rfc/rfc5626),
[RFC 6223 (`keep` param)](https://www.rfc-editor.org/rfc/rfc6223.html),
[RFC 6455 §5.5.2 (WS Ping)](https://www.rfc-editor.org/rfc/rfc6455),
[Google SAPISIDHASH / session auth (DeepWiki)](https://deepwiki.com/one880808/gemini-web2api/4.2-google-session-authentication-(sapisidhash-and-cookie)),
[accounts.google.com/RotateCookies (gist)](https://gist.github.com/szv99/f78c032736443fab51075bc45f9faf09),
[gemini-webapi auto cookie refresh](https://pypi.org/project/gemini-webapi/),
[HanaokaYuzu/Gemini-API __Secure-1PSIDTS issue](https://github.com/HanaokaYuzu/Gemini-API/issues/6),
[JsSIP SIP-over-WebSocket](https://jssip.net/documentation/misc/sip_websocket/).
