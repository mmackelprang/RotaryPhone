# PR5 Plan — `feat(gv): inter-service auth gate (X-RotaryPhone-Auth)`

> ## 🔒 OWNER-HOLD — DO NOT BUILD OR MERGE WITHOUT EXPLICIT OWNER APPROVAL
> **Reason:** This PR **touches auth/secret handling** — it introduces a shared secret that gates the
> credential-adjacent `/api/gvbridge/cookies` endpoints, private voicemail audio, and the SMS-send
> write path, across the RotaryPhone↔RadioConsole service boundary. ADR §6.5, §10 row PR5, §12 #2, and
> CLAUDE.md ("Still pause … touches something sensitive (auth, secrets, …)") all require owner sign-off
> to **build and to merge**. Writing this plan is what was requested now; **building it is gated.** The
> owner must merge this PR personally (not auto-merge). It is also the **prerequisite for any non-LAN
> exposure** (port-forward / VPN / Tailscale) of RotaryPhone — see the boundary doc Cookie Management
> Security note.

> **For agentic workers (once unheld):** REQUIRED SUB-SKILL — use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Arc:** `docs/plans/gv-voicemail-sms-arc.md` (phase-log row 4e)
**ADR (source of truth):** `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md`
— §6.5 (inter-service auth gate), §10 row PR5, §11 step 7 (auth-gate smoke), §12 #2.
**Boundary doc:** `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md` (Cookie Management Security note +
Integration Points table + Change Log — all updated by this PR).
**Handoff:** `docs/handoffs/radioconsole-gv-voicemail-sms-ui-handoff.md` (currently "do not send the
header yet" — this PR defines when/how RadioConsole flips that on).
**Depends on:** PR2 (voicemail endpoints), PR4 (`POST /sms/send`) for the full endpoint surface to gate
— but the middleware is **endpoint-agnostic** (gates the whole `/api/gvbridge/*` prefix), so it does not
hard-depend on PR4's internals.
**Sensitivity:** 🔒 **HOLD — owner review** (auth/secret handling + cross-service contract).

---

## Goal

Add a **single, uniform, default-off** shared-secret gate so that, **when and only when a key is
configured**, every `/api/gvbridge/*` REST endpoint **and** the SignalR hub require
`X-RotaryPhone-Auth: <key>`; without the key configured, behavior is **byte-identical to today**
(LAN-only, no auth). Specifically:

1. **`GVBridgeConfig.InterServiceAuthKey`** (default `""` = disabled). When empty → the gate is inert
   and nothing changes (zero-behavior-change guarantee).
2. **REST middleware** requiring the header on **all `/api/gvbridge/*`** routes — voicemail, SMS read,
   the PR4 `/sms/send` write, the existing `/cookies` + `/cookies/refresh-from-browser` + `/status` +
   `/adapter/mode` endpoints — returning **401** with constant-time comparison on mismatch.
3. **SignalR hub gate** on `/hub` — the negotiate/connect must carry the key (header on REST transports,
   access-token on the WebSocket query for browsers), enforced by the same key, same default-off rule.
4. **Boundary-doc + handoff updates** describing the contract so the RadioConsole session can flip its
   header on (it is explicitly told "don't send the header yet" today).
5. **RadioConsole-side contract described, NOT built** — that is a cross-repo task in the RTest repo
   (memory `project_ui_integration.md`); this plan specifies the contract precisely and flags it.

## Design constraint: one gate, applied consistently (ADR §6.5)

The ADR is emphatic that this is "a config + middleware change, **not a redesign**" and "**one gate,
applied consistently**." So: a single piece of middleware keyed off one config value, covering both the
REST prefix and the hub. No per-controller `[Authorize]` attributes, no ASP.NET Identity, no JWT — this
is a fixed shared secret between two services on the same box. Constant-time comparison to avoid timing
oracles on the key.

## Why both REST and the hub (ADR §6.3, §6.5)

RadioConsole consumes RotaryPhone over **REST + the SignalR hub**. Gating only REST would leave the
push channel (which now carries SMS/voicemail content via `SmsReceived`/`VoicemailReceived`/`SmsSent`)
open. Both must be gated by the same key, or the gate is theater. The hub already lives at `/hub`
(`app.MapHub<RotaryHub>("/hub")` in `Program.cs`) and currently carries `CallStateChanged`/`IncomingCall`
too — gating it also protects those, which is acceptable and desirable for non-LAN exposure.

---

## File Structure

### New files (in `src/RotaryPhoneController.Server/`)

```
Middleware/
  GvBridgeAuthMiddleware.cs      -- gates /api/gvbridge/* (REST). Lives in Server (owns the pipeline).
Auth/
  InterServiceAuthValidator.cs   -- constant-time key check, shared by REST middleware + hub filter
Hubs/
  HubAuthFilter.cs               -- IHubFilter / OnConnectedAsync gate for RotaryHub
```

> **Why Server, not GVBridge:** the middleware/hub gate hooks the ASP.NET pipeline and `RotaryHub`,
> both of which live in `RotaryPhoneController.Server` (GVBridge is deliberately UI-framework-free —
> see `GVBridgeServiceExtensions` comments and the PR3 `GvMessagePushBridge` precedent). The key itself
> is read from `GVBridgeConfig` (bound in GVBridge), so the validator takes the key value, not the
> config object, keeping the dependency one-directional.

### New test files

```
src/RotaryPhoneController.Server.Tests/        (create the test project if it does not exist — see Task 1)
  Auth/InterServiceAuthValidatorTests.cs
  Middleware/GvBridgeAuthMiddlewareTests.cs
  Hubs/HubAuthFilterTests.cs
```

### Modified files

```
src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs      -- InterServiceAuthKey (default "")
src/RotaryPhoneController.Server/appsettings.json                -- InterServiceAuthKey: "" + secret note
src/RotaryPhoneController.Server/Program.cs                      -- register validator, app.UseMiddleware, AddHubFilter
docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md                  -- Integration Points + Cookie Security + Change Log
docs/handoffs/radioconsole-gv-voicemail-sms-ui-handoff.md        -- flip "don't send the header yet" → how/when to send
```

---

## Task 1: Confirm/create the Server test project

> The GVBridge has a `.Tests` project; the Server project may not. The middleware + hub gate live in
> Server, so they need a Server test project. **Implementer:** check
> `src/RotaryPhoneController.Server.Tests/RotaryPhoneController.Server.Tests.csproj`. If absent, create
> a minimal xUnit project referencing `RotaryPhoneController.Server` +
> `Microsoft.AspNetCore.Mvc.Testing` and add it to the solution.

- [ ] **Step 1: Probe** — `ls src/RotaryPhoneController.Server.Tests 2>/dev/null` (or Glob).
- [ ] **Step 2: If missing, create the project** (copy the GVBridge.Tests csproj as a template, retarget
  the project reference to `RotaryPhoneController.Server`, add `Microsoft.AspNetCore.Mvc.Testing` and
  `Microsoft.AspNetCore.SignalR` test deps), then `dotnet sln add` it.
- [ ] **Step 3: Build the (empty) test project** → succeeded.
- [ ] **Step 4: Commit**

```bash
git add src/RotaryPhoneController.Server.Tests/ RotaryPhoneController.sln
git commit -m "test: scaffold Server test project for inter-service auth gate (PR5)"
```

---

## Task 2: InterServiceAuthValidator (constant-time check) — TDD

**Files:**
- Create: `src/RotaryPhoneController.Server.Tests/Auth/InterServiceAuthValidatorTests.cs`
- Create: `src/RotaryPhoneController.Server/Auth/InterServiceAuthValidator.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using RotaryPhoneController.Server.Auth;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Auth;

public class InterServiceAuthValidatorTests
{
    [Fact]
    public void EmptyConfiguredKey_GateDisabled_AlwaysAllows()
    {
        var v = new InterServiceAuthValidator(configuredKey: "");
        Assert.False(v.IsEnabled);
        Assert.True(v.IsAuthorized(null));        // no header, gate off → allow (today's LAN behavior)
        Assert.True(v.IsAuthorized("anything"));
    }

    [Fact]
    public void ConfiguredKey_CorrectHeader_Authorized()
    {
        var v = new InterServiceAuthValidator(configuredKey: "s3cret-key");
        Assert.True(v.IsEnabled);
        Assert.True(v.IsAuthorized("s3cret-key"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("s3cret-ke")]      // prefix
    [InlineData("s3cret-key ")]    // trailing space
    [InlineData("S3CRET-KEY")]     // case-sensitive
    public void ConfiguredKey_MissingOrWrongHeader_NotAuthorized(string? header)
    {
        var v = new InterServiceAuthValidator(configuredKey: "s3cret-key");
        Assert.False(v.IsAuthorized(header));
    }
}
```

- [ ] **Step 2: Run → FAIL**

- [ ] **Step 3: Implement**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace RotaryPhoneController.Server.Auth;

/// <summary>
/// Validates the X-RotaryPhone-Auth shared secret (ADR §6.5). When the configured key is empty the gate
/// is DISABLED and everything is allowed — preserving today's LAN-only, no-auth behavior exactly (zero
/// behavior change when unset). When set, the supplied header must match exactly, compared in
/// CONSTANT TIME (CryptographicOperations.FixedTimeEquals) so the key cannot be recovered via a timing
/// oracle. Required before any non-LAN exposure (boundary doc Cookie Management Security note).
/// </summary>
public class InterServiceAuthValidator
{
    private readonly byte[] _keyBytes;

    public InterServiceAuthValidator(string configuredKey)
    {
        IsEnabled = !string.IsNullOrEmpty(configuredKey);
        _keyBytes = Encoding.UTF8.GetBytes(configuredKey ?? "");
    }

    /// <summary>True if a non-empty key is configured (the gate enforces).</summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Gate decision. Disabled → always true. Enabled → constant-time exact match of the header bytes.
    /// </summary>
    public bool IsAuthorized(string? presentedKey)
    {
        if (!IsEnabled) return true;
        if (presentedKey is null) return false;
        var presented = Encoding.UTF8.GetBytes(presentedKey);
        return CryptographicOperations.FixedTimeEquals(presented, _keyBytes);
    }
}
```

> `FixedTimeEquals` returns false immediately on length mismatch (that length leak is acceptable — it
> reveals key length, not content; the threat model is a LAN/exposed-port attacker, and the key should
> be long-random regardless). This matches standard practice for fixed shared secrets.

- [ ] **Step 4: Run → PASS** (8 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Server/Auth/InterServiceAuthValidator.cs \
        src/RotaryPhoneController.Server.Tests/Auth/InterServiceAuthValidatorTests.cs
git commit -m "feat(gvbridge): constant-time X-RotaryPhone-Auth validator, default-off (ADR §6.5)"
```

---

## Task 3: GvBridgeAuthMiddleware (gates /api/gvbridge/*) — TDD

**Files:**
- Create: `src/RotaryPhoneController.Server.Tests/Middleware/GvBridgeAuthMiddlewareTests.cs`
- Create: `src/RotaryPhoneController.Server/Middleware/GvBridgeAuthMiddleware.cs`

- [ ] **Step 1: Write failing tests** (drive the middleware directly with a `DefaultHttpContext`)

```csharp
using Microsoft.AspNetCore.Http;
using RotaryPhoneController.Server.Auth;
using RotaryPhoneController.Server.Middleware;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Middleware;

public class GvBridgeAuthMiddlewareTests
{
    private static async Task<int> Invoke(string path, string? header, string configuredKey)
    {
        var validator = new InterServiceAuthValidator(configuredKey);
        var nextCalled = false;
        var mw = new GvBridgeAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, validator);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (header is not null) ctx.Request.Headers["X-RotaryPhone-Auth"] = header;
        await mw.InvokeAsync(ctx);
        // Encode "passed through" as 200-from-next, else the status the middleware set.
        return nextCalled ? 200 : ctx.Response.StatusCode;
    }

    [Fact] public async Task GateOff_AllowsWithoutHeader()
        => Assert.Equal(200, await Invoke("/api/gvbridge/sms/threads", header: null, configuredKey: ""));

    [Fact] public async Task GateOn_CorrectHeader_PassesThrough()
        => Assert.Equal(200, await Invoke("/api/gvbridge/sms/send", "k", configuredKey: "k"));

    [Fact] public async Task GateOn_MissingHeader_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/cookies", header: null, configuredKey: "k"));

    [Fact] public async Task GateOn_WrongHeader_401()
        => Assert.Equal(401, await Invoke("/api/gvbridge/voicemail", "nope", configuredKey: "k"));

    [Fact] public async Task GateOn_NonGvBridgePath_NotGated()
        => Assert.Equal(200, await Invoke("/api/phone/status", header: null, configuredKey: "k"));

    [Fact] public async Task GateOn_GvBridgeEventPath_NotGated()  // extension content-script endpoint stays open
        => Assert.Equal(200, await Invoke("/api/gvbridge/event", header: null, configuredKey: "k"));
}
```

- [ ] **Step 2: Run → FAIL**

- [ ] **Step 3: Implement**

```csharp
using RotaryPhoneController.Server.Auth;

namespace RotaryPhoneController.Server.Middleware;

/// <summary>
/// Gates every /api/gvbridge/* REST endpoint behind X-RotaryPhone-Auth when a key is configured
/// (ADR §6.5). Default-off: with no key, this is a pass-through and today's LAN behavior is unchanged.
/// EXCEPTION: /api/gvbridge/event stays open — it is the browser-extension content-script callback
/// (CORS-handled in Program.cs), not a RadioConsole consumer endpoint, and gating it would break the
/// extension. All other /api/gvbridge/* paths (status, adapter/mode, cookies, voicemail, sms, sms/send)
/// are gated uniformly — "one gate, applied consistently" (ADR §6.5).
/// </summary>
public class GvBridgeAuthMiddleware
{
    public const string HeaderName = "X-RotaryPhone-Auth";
    private readonly RequestDelegate _next;
    private readonly InterServiceAuthValidator _validator;

    public GvBridgeAuthMiddleware(RequestDelegate next, InterServiceAuthValidator validator)
    {
        _next = next;
        _validator = validator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_validator.IsEnabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var isGvBridge = path.StartsWith("/api/gvbridge", StringComparison.OrdinalIgnoreCase);
        var isExtensionEvent = path.Contains("/gvbridge/event", StringComparison.OrdinalIgnoreCase);

        if (isGvBridge && !isExtensionEvent)
        {
            var header = context.Request.Headers[HeaderName].ToString();
            if (!_validator.IsAuthorized(string.IsNullOrEmpty(header) ? null : header))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"Missing or invalid X-RotaryPhone-Auth header"}""");
                return;
            }
        }

        await _next(context);
    }
}
```

- [ ] **Step 4: Run → PASS** (6 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Server/Middleware/GvBridgeAuthMiddleware.cs \
        src/RotaryPhoneController.Server.Tests/Middleware/GvBridgeAuthMiddlewareTests.cs
git commit -m "feat(gvbridge): REST auth middleware for /api/gvbridge/* (401, default-off)"
```

---

## Task 4: HubAuthFilter (gates the SignalR hub) — TDD

> SignalR clients cannot always set arbitrary headers on the WebSocket handshake (browsers can't), so
> the canonical pattern is: REST/long-poll transports send the header; the WebSocket transport sends
> the key as an `access_token` query-string param, which SignalR surfaces on the connect context. We
> accept **either** the header (non-browser/.NET clients — which is RadioConsole's case, a .NET
> `HubConnection`) **or** the `access_token` query param, and validate with the same validator.

**Files:**
- Create: `src/RotaryPhoneController.Server.Tests/Hubs/HubAuthFilterTests.cs`
- Create: `src/RotaryPhoneController.Server/Hubs/HubAuthFilter.cs`

- [ ] **Step 1: Write failing tests** (test the pure decision function; the `IHubFilter` wrapper is thin)

```csharp
using Microsoft.AspNetCore.Http;
using RotaryPhoneController.Server.Auth;
using RotaryPhoneController.Server.Hubs;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Hubs;

public class HubAuthFilterTests
{
    private static HttpContext Ctx(string? header, string? token)
    {
        var c = new DefaultHttpContext();
        if (header is not null) c.Request.Headers["X-RotaryPhone-Auth"] = header;
        if (token is not null) c.Request.QueryString = new QueryString($"?access_token={token}");
        return c;
    }

    [Fact] public void GateOff_AllowsAnyConnection()
        => Assert.True(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator(""), Ctx(null, null)));

    [Fact] public void GateOn_HeaderMatch_Allows()
        => Assert.True(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx("k", null)));

    [Fact] public void GateOn_AccessTokenMatch_Allows()
        => Assert.True(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx(null, "k")));

    [Fact] public void GateOn_NoCredential_Denies()
        => Assert.False(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx(null, null)));

    [Fact] public void GateOn_WrongCredential_Denies()
        => Assert.False(HubAuthFilter.IsConnectionAuthorized(new InterServiceAuthValidator("k"), Ctx("nope", "nope")));
}
```

- [ ] **Step 2: Run → FAIL**

- [ ] **Step 3: Implement**

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using RotaryPhoneController.Server.Auth;

namespace RotaryPhoneController.Server.Hubs;

/// <summary>
/// Gates SignalR hub connections behind X-RotaryPhone-Auth when a key is configured (ADR §6.5, §6.3).
/// Accepts the header (non-browser/.NET HubConnection — RadioConsole's case) OR an access_token query
/// param (browser WebSocket, which cannot set headers on the handshake). Default-off: with no key,
/// every connection is allowed (today's behavior). Denied connections are aborted at connect time, so
/// no SMS/voicemail/call event ever reaches an unauthenticated client.
/// </summary>
public class HubAuthFilter : IHubFilter
{
    public const string HeaderName = "X-RotaryPhone-Auth";
    private readonly InterServiceAuthValidator _validator;

    public HubAuthFilter(InterServiceAuthValidator validator) => _validator = validator;

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var http = context.Context.GetHttpContext();
        if (http is null || !IsConnectionAuthorized(_validator, http))
        {
            context.Context.Abort();
            return;
        }
        await next(context);
    }

    /// <summary>Pure decision: header OR access_token must match when the gate is enabled.</summary>
    public static bool IsConnectionAuthorized(InterServiceAuthValidator validator, HttpContext http)
    {
        if (!validator.IsEnabled) return true;
        var header = http.Request.Headers[HeaderName].ToString();
        if (!string.IsNullOrEmpty(header) && validator.IsAuthorized(header)) return true;
        var token = http.Request.Query["access_token"].ToString();
        return !string.IsNullOrEmpty(token) && validator.IsAuthorized(token);
    }
}
```

- [ ] **Step 4: Run → PASS** (5 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.Server/Hubs/HubAuthFilter.cs \
        src/RotaryPhoneController.Server.Tests/Hubs/HubAuthFilterTests.cs
git commit -m "feat(gvbridge): SignalR hub auth filter (header or access_token, default-off)"
```

---

## Task 5: Config + Program.cs wiring + secret handling

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`
- Modify: `src/RotaryPhoneController.Server/Program.cs`

- [ ] **Step 1: Add the config key** to `GVBridgeConfig` (after the SMS-send block):

```csharp
    // Inter-service auth gate (ADR §6.5). EMPTY = DISABLED (LAN-only, no auth — exactly as today).
    // When set, X-RotaryPhone-Auth: <key> is REQUIRED on all /api/gvbridge/* endpoints AND the SignalR
    // hub. REQUIRED before any non-LAN exposure (port-forward/VPN/Tailscale). Store the actual secret
    // OUTSIDE source — see appsettings note (env var / user-secrets), never commit a real key. (PR5.)
    public string InterServiceAuthKey { get; set; } = "";
```

- [ ] **Step 2: Add the key to `appsettings.json`** with a secret-handling note. The committed value is
  **empty** (gate off). Document the override mechanisms so no secret lands in source:

```json
"InterServiceAuthKey": ""
```

  And add a sibling doc comment in the boundary-doc + handoff (Tasks 6/7) — appsettings.json can't hold
  JSON comments, so the secret-handling guidance lives in the boundary doc. The actual key is supplied
  at runtime via the **standard ASP.NET configuration layering already in use** (no new mechanism):
  - **Production (the `radio` box):** environment variable
    `GVBridge__InterServiceAuthKey=<key>` (the `__` double-underscore convention this project's
    `builder.Configuration` already honors via `Configure<GVBridgeConfig>(... "GVBridge")`), set in the
    systemd unit's `Environment=`/`EnvironmentFile=` (out of the repo).
  - **Local dev:** `dotnet user-secrets` keyed `GVBridge:InterServiceAuthKey`.
  - **Never** commit a real key; the in-repo value stays `""`.

> **Investigate-and-match note (do at build time):** confirm whether the systemd unit on `radio`
> already uses an `EnvironmentFile=` (the deploy scripts / `docs/` may show one). Reuse it — match the
> existing secret mechanism rather than inventing one. `CookieEncryptionKey` in `GVBridgeConfig` is the
> existing precedent for "secret-shaped config kept empty in source, supplied at runtime"; follow the
> same operational pattern it uses.

- [ ] **Step 3: Wire the validator + middleware + hub filter in `Program.cs`.**

Register the validator from config (singleton), add the REST middleware to the pipeline **before**
`app.MapControllers()`/`MapGVBridge()`, and register the hub filter on `AddSignalR`:

```csharp
// (a) where AddSignalR() is called (currently builder.Services.AddSignalR();) — add the filter:
builder.Services.AddSignalR(options =>
    options.AddFilter<RotaryPhoneController.Server.Hubs.HubAuthFilter>());

// (b) register the validator (after AddGVBridge so GVBridgeConfig is bound):
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
        RotaryPhoneController.GVBridge.Models.GVBridgeConfig>>().Value;
    return new RotaryPhoneController.Server.Auth.InterServiceAuthValidator(cfg.InterServiceAuthKey);
});
builder.Services.AddSingleton<RotaryPhoneController.Server.Hubs.HubAuthFilter>();

// (c) in the pipeline, BEFORE app.MapControllers()/MapGVBridge() and AFTER UseCors:
app.UseMiddleware<RotaryPhoneController.Server.Middleware.GvBridgeAuthMiddleware>();
```

> Placement matters: the middleware must run after CORS (so preflight still works) and before the
> endpoints. The existing `app.Use(...)` block that handles `gvbridge/event` CORS stays — and the
> middleware explicitly exempts `/gvbridge/event`, so the extension path is unaffected. The `IHubFilter`
> is global (all hubs); RotaryHub is the only hub, so this is correct. If more hubs are added later,
> revisit (note in the Change Log).

- [ ] **Step 4: Build the Server project** → succeeded.
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs \
        src/RotaryPhoneController.Server/appsettings.json \
        src/RotaryPhoneController.Server/Program.cs
git commit -m "feat(gvbridge): wire X-RotaryPhone-Auth gate (REST middleware + hub filter), default-off"
```

---

## Task 6: Boundary-doc update (REQUIRED — protocol)

> Per `CLAUDE.md` and memory `feedback_boundary_doc_protocol.md`: **update the boundary doc before/with
> any cross-service contract change.** This PR changes the contract (a new required header under
> non-LAN exposure), so the boundary doc MUST reflect it.

**Files:** Modify `docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md`

- [ ] **Step 1: Integration Points table** — add a header column note / a new line documenting that all
  `/api/gvbridge/*` endpoints + the `/hub` connection accept an **optional** `X-RotaryPhone-Auth`
  header (required when `InterServiceAuthKey` is set). Add the PR4 `POST /api/gvbridge/sms/send` and the
  voicemail/SMS read endpoints (PR2/PR3) to the table if not already present, each marked "auth-gated
  when key set."
- [ ] **Step 2: Cookie Management Security note** — update it to state the gap it flagged
  (`/api/gvbridge/cookies` LAN-only, no auth) is now **closeable**: setting `InterServiceAuthKey`
  enforces the header on the cookie endpoints too. Note the gate is **required before any non-LAN
  exposure**.
- [ ] **Step 3: Change Log** — append a dated row:

```
| 2026-06-20 | RotaryPhone session | PR5: inter-service auth gate. New optional `GVBridge:InterServiceAuthKey` (default empty = LAN-only, no behavior change). When set, `X-RotaryPhone-Auth: <key>` is REQUIRED on all `/api/gvbridge/*` REST endpoints AND the `/hub` SignalR connection (header, or access_token query for browser WS); 401/abort otherwise; constant-time compare. `/api/gvbridge/event` (extension content-script) stays open. Secret supplied at runtime via env (`GVBridge__InterServiceAuthKey`) / user-secrets — never committed. REQUIRED before any non-LAN exposure of RotaryPhone. RadioConsole must send the header on REST + an access-token provider on its HubConnection once the key is set (cross-repo; handoff updated). |
```

- [ ] **Step 4: Commit**

```bash
git add docs/prompts/RADIO-CONSOLE-BT-AUDIO-BOUNDARY.md
git commit -m "docs(boundary): document X-RotaryPhone-Auth gate + secret handling (PR5)"
```

---

## Task 7: Handoff update — RadioConsole-side contract (cross-repo, DESCRIBE only)

> The active RadioConsole UI is in the **RTest repo** (memory `project_ui_integration.md`) — we do NOT
> build the RadioConsole side here. We update the handoff so the RadioConsole session knows exactly how
> to flip on the header it is currently told NOT to send.

**Files:** Modify `docs/handoffs/radioconsole-gv-voicemail-sms-ui-handoff.md`

- [ ] **Step 1:** Update the "Auth / networking posture" + "What is NOT built yet (#3)" sections from
  "do not send the header yet" to the **as-built contract**:
  - **REST:** when `InterServiceAuthKey` is configured on RotaryPhone, send
    `X-RotaryPhone-Auth: <key>` on **every** `/api/gvbridge/*` request. Read the key from RadioConsole's
    own config (its existing config/secret mechanism — not hard-coded). Without it, a gated RotaryPhone
    returns **401**.
  - **SignalR:** supply the key on the `HubConnection`. For a .NET `HubConnectionBuilder`, the
    canonical approach is `options.Headers["X-RotaryPhone-Auth"] = key` (works for the .NET client on
    all transports) and/or `options.AccessTokenProvider = () => Task.FromResult(key)` (surfaces as the
    `access_token` query param, which RotaryPhone's `HubAuthFilter` also accepts). Either satisfies the
    gate; the header is simplest for the .NET client.
  - **Rollout:** the key is **default-off**; both services keep working with no key (LAN). To enable:
    set the same key on both services' config out-of-source, then RadioConsole starts sending it. A
    mismatch = 401 (REST) / aborted connection (hub) → RadioConsole should surface the existing
    "reconnecting/unavailable" banner (handoff §7), not a hard crash.
- [ ] **Step 2:** Add to the handoff's "Open a thread back to the RotaryPhone side for" list:
  the agreed key value + the exact env var name on each box (out-of-band, never in a repo).
- [ ] **Step 3: Commit**

```bash
git add docs/handoffs/radioconsole-gv-voicemail-sms-ui-handoff.md
git commit -m "docs(handoff): RadioConsole X-RotaryPhone-Auth contract (REST + hub access-token)"
```

---

## Task 8: Full suite + completion gate

- [ ] **Step 1: Server test suite** — `dotnet test src/RotaryPhoneController.Server.Tests -v n` → green
  (validator + middleware + hub filter).
- [ ] **Step 2: GVBridge suite still green** — `dotnet test src/RotaryPhoneController.GVBridge.Tests -v n`
  (the config-only change must not break anything).
- [ ] **Step 3: Server build** → succeeded.
- [ ] **Step 4: Default-off regression check (the zero-behavior-change guarantee).** With
  `InterServiceAuthKey` empty (committed default): confirm an integration test (or manual smoke) that a
  `/api/gvbridge/status` GET with NO header still returns 200 and the hub still connects — i.e. **today's
  LAN behavior is byte-identical.** This is the single most important gate: an accidental
  default-on would break the live RadioConsole↔RotaryPhone link.
- [ ] **Step 5: No browser UAT here** (RadioConsole UI in RTest repo, per PR1–4). Backend gates =
  the test suites + the live smoke below.
- [ ] **Step 6: Live verification (owner/Tester on the `radio` box) — ADR §11 step 7.** ONLY after the
  owner green-lights the build, and with a key set on RotaryPhone:
  - `/api/gvbridge/status` (and `/cookies`, `/sms/threads`, `/sms/send`) → **401 without** the header,
    **200 with** the correct header.
  - SignalR: a HubConnection **without** the key is aborted; **with** the key connects and receives
    `SmsReceived`/`IncomingCall`.
  - Set the matching key on RadioConsole (RTest) → confirm it reaches all endpoints + the hub again.
  - Unset the key on RotaryPhone → confirm LAN behavior returns (no header needed).

---

## Out of scope for PR5 (do NOT do here)

- **No RadioConsole-side implementation** — that is the RTest repo (cross-repo). This PR only
  **describes** the contract in the handoff + boundary doc.
- **No per-user / per-client auth, no rotating tokens, no JWT/OAuth** — ADR §6.5 is explicit: one fixed
  shared secret, one gate. Anything richer is a future ADR, not this PR.
- **No change to the `/api/gvbridge/event` extension endpoint** — it stays open (content-script CORS).
- **No mTLS / TLS termination changes** — the gate is application-layer; transport security for non-LAN
  exposure (the tunnel/VPN itself) is a separate operational concern.
- **No secret committed to source** — the in-repo `InterServiceAuthKey` stays `""`.

---

## ADR §12 decision points flagged for the owner (do not block — plan against defaults)

1. **§12 #2 — enable the gate now?** This plan ships the gate **default-OFF** (the ADR's recommendation:
   "add it now, default-off to preserve LAN behavior"). **If the owner wants it ON immediately,** set
   `InterServiceAuthKey` on **both** RotaryPhone and RadioConsole (cross-repo, coordinated) — the code
   needs no change, only config. Do not turn it on unilaterally: a one-sided enable = instant 401 storm
   from RadioConsole. Flag this coordination explicitly to the owner before flipping.
2. **Secret mechanism.** Plan assumes the existing env-var / user-secrets layering (matching
   `CookieEncryptionKey`'s pattern). **If the owner uses a different secret store** (e.g. a
   `.env`/EnvironmentFile already on `radio`), point `InterServiceAuthKey` at that — investigate at
   build time and match; do not introduce a new mechanism.
3. **Non-LAN exposure timing.** The gate is the **prerequisite** for port-forward/VPN/Tailscale. If the
   owner intends to expose RotaryPhone beyond the LAN, the gate must be ON first; if it stays LAN-only,
   the gate can remain off indefinitely with no downside. Surface which the owner intends.
