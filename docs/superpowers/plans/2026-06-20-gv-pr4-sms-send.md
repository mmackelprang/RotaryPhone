# PR4 Plan — `feat(gv): SMS send`

> ## 🔒 OWNER-HOLD — DO NOT BUILD OR MERGE WITHOUT EXPLICIT OWNER APPROVAL
> **Reason:** This PR introduces an **irreversible, user-visible GV account write** (`sendsms` actually
> delivers a text from the owner's real Google Voice number). The ADR (§4.2 #4, §10 row PR4, §12 #1)
> classes it as a HOLD-for-owner item under the auto-merge policy. **Writing this plan is what was
> requested; building it is gated on the owner saying "go."** Do not let a Builder pick this up off the
> normal queue. The owner must explicitly green-light the build, and the PR must be merged by the owner
> (not auto-merged on green gates), per CLAUDE.md "Still pause and check with me first … irreversible …".

> **For agentic workers (once unheld):** REQUIRED SUB-SKILL — use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement task-by-task. Steps use checkbox (`- [ ]`)
> syntax for tracking.

**Arc:** `docs/plans/gv-voicemail-sms-arc.md` (phase-log row 4d)
**ADR (source of truth):** `docs/architecture/decisions/2026-06-20-gv-voicemail-sms-radioconsole.md`
— §4 (esp. §4.1 send payload, §4.2 send safety rules), §6.1 (`SendSmsRequest`/`SendSmsResponse`),
§6.2 (the `POST /api/gvbridge/sms/send` row), §10 row PR4, §11 step 4 (live capture), §12 #1.
**Depends on:** PR3 (`GvSmsClient` read path, `GvThreadClient`, parser seam, `IGvAuthenticatedClientProvider`,
`GvThreadPoller`, `RotaryHub` `SmsReceived` push) — **all merged** (#54/#56/#57).
**Sensitivity:** 🔒 **HOLD — owner review** (irreversible GV account **write**).

---

## Goal

Add the one capability the read-side arc deliberately left out: **sending / replying to a text**
through the owner's GV number. Concretely:

1. **`GvSmsClient.SendAsync(toNumber, text, threadId?)`** — `POST api2thread/sendsms` over the **shared
   authenticated `HttpClient`** (same seam every read client rides), payload per ADR §4.1
   `[null,null,null,null,"<text>","<threadId>"]`. The thread-id derivation lives behind a **seam +
   fixtures** carrying the same **UNVERIFIED — pending ADR §11 live capture** caveat as the read parsers.
2. **`POST /api/gvbridge/sms/send`** on the existing `GvSmsController`, taking `SendSmsRequest` and
   returning `SendSmsResponse` (ADR §6.1). On success it returns the created **outbound**
   `SmsMessageDto` so RadioConsole can echo it, **and** broadcasts it over `RotaryHub` (`SmsSent`) so
   every connected client converges (decision recorded in Task 6).
3. **The §4.2 safety rules — the entire reason this PR is owner-held** — implemented explicitly:
   E.164 normalization/validation, server-side rate-limiting (429), **no auto-retry**, honest status
   that never over-claims delivery, and correct new-recipient-vs-reply thread-id derivation.

## The honesty constraint (read first — this project has a dishonest-status incident)

Memory `project_gv_registration_603_incident.md`: a previous "rings but no audio" bug traced to
**status that over-claimed success**. Apply the same discipline here. Per ADR §4.2 #3, `sendsms`
returns a **transaction ack, not the echoed message** — so:

- A 200 from Google means **queued**, *not* "delivered." `SendSmsResponse.Queued = true` means exactly
  "Google accepted the send request," nothing stronger. Never report delivery we cannot observe.
- The returned outbound `SmsMessageDto` is a **locally-synthesized optimistic echo** (text we sent +
  the resolved thread id + `Direction = "Outbound"`), clearly the request we made — not a parse of
  Google's response body. The **authoritative** copy of the message arrives later via the normal
  `GvThreadPoller` diff (ADR §4.2 #3) and the existing `SmsReceived`/read endpoints.
- On any non-200 or ambiguous failure: `Queued = false`, populate `Error`, **do not retry** (ADR §4.2
  #4). The UI owns the user's retry decision (handoff: "preserve the typed text … never auto-retry").

## The stable seam: thread-id derivation (ADR §4.2 #1, UNVERIFIED — §11 step 4)

The real unknown in this PR is **which thread id to send to**. ADR §4.2 #1: a *new* 1:1 conversation
uses `t.+<E164>`; a *reply* should use the **thread id Google already assigned** (from the read list),
which may be an opaque `t.<hash>`. We isolate that rule behind one interface so the live-capture
correction (ADR §11 step 4: "capture the thread id GV assigned and test reply vs `t.+<E164>`") is a
one-file change — exactly the discipline the read parsers use (`IGvThreadParser`).

- `ISmsThreadIdResolver.Resolve(normalizedE164, explicitThreadId?)` →
  - if `explicitThreadId` is a non-empty real GV id (reply path) → use it verbatim.
  - else (new conversation) → synthesize `t.+<E164>` (the ADR §4.1 form).
- The synthesized form is **UNVERIFIED** and tagged so in code + tests, identical to
  `GvThreadFolder.ToWireValue()` and the positional parser indices.

---

## File Structure

### New files (in `src/RotaryPhoneController.GVBridge/`)

```
Clients/
  PhoneNumberNormalizer.cs       -- E.164 normalize/validate (no existing helper in this project — verified)
  ISmsThreadIdResolver.cs        -- the thread-id seam (reply id vs synthesized t.+<E164>)
  SmsThreadIdResolver.cs         -- default impl; synthesized form UNVERIFIED pending ADR §11 step 4
  SmsSendRateLimiter.cs          -- sliding-window limiter (reject > N sends / 10s → 429)
Api/
  GvBridgeReadDtos.cs            -- (extend) add SendSmsRequest / SendSmsResponse (ADR §6.1)
```

### Modified files

```
Clients/GvSmsClient.cs                          -- add SendAsync (the write path; read path untouched)
Api/GvSmsController.cs                           -- add [HttpPost("send")]
Models/GVBridgeConfig.cs                         -- rate-limit config keys
Extensions/GVBridgeServiceExtensions.cs          -- register normalizer, resolver, rate-limiter
src/RotaryPhoneController.Server/appsettings.json -- rate-limit config keys
src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs -- forward a new SmsSent event (Task 6)
```

### New test files

```
src/RotaryPhoneController.GVBridge.Tests/
  Clients/PhoneNumberNormalizerTests.cs
  Clients/SmsThreadIdResolverTests.cs
  Clients/SmsSendRateLimiterTests.cs
  Clients/GvSmsClientSendTests.cs
  Api/GvSmsControllerSendTests.cs
```

---

## Task 1: Send DTOs (ADR §6.1) — extend the shared file

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs`

- [ ] **Step 1: Append the send DTOs** (the read DTOs already live here from PR3)

Add to `src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs`, exactly as ADR §6.1:

```csharp
/// <summary>
/// Cross-service SMS send request (ADR §6.1, §4). ToNumber is whatever the user typed; RotaryPhone
/// normalizes it to E.164 before building the GV thread id. ThreadId is OPTIONAL: present = reply to
/// an existing thread (use Google's real id); null/empty = start a new conversation (ADR §4.2 #1).
/// </summary>
public record SendSmsRequest(string ToNumber, string Text, string? ThreadId);

/// <summary>
/// Cross-service SMS send result (ADR §6.1, §4.2 #3). Queued=true means GOOGLE ACCEPTED the send —
/// NOT confirmed delivery (sendsms returns a transaction ack, not the echoed message). On failure,
/// Queued=false and Error is populated; the caller must NOT auto-retry (ADR §4.2 #4). Message is a
/// locally-synthesized optimistic echo of the outbound text (Direction="Outbound"); the authoritative
/// copy arrives later via the GvThreadPoller diff.
/// </summary>
public record SendSmsResponse(bool Queued, string? ThreadId, string? Error, SmsMessageDto? Message);
```

- [ ] **Step 2: Build** — `dotnet build src/RotaryPhoneController.GVBridge/RotaryPhoneController.GVBridge.csproj` → Build succeeded.
- [ ] **Step 3: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvBridgeReadDtos.cs
git commit -m "feat(gv): add SMS send DTOs (SendSmsRequest/SendSmsResponse, ADR §6.1)"
```

---

## Task 2: PhoneNumberNormalizer (E.164) — TDD (ADR §4.2 #2)

> **Why a new helper:** a repo scan found **no existing E.164/phone normalizer** in the GVBridge project
> (the ADR §4.2 #2 phrase "reuse existing dialing normalization" refers to a helper that does not
> actually exist here — corrected, same spirit as the ADR's own §1.2 "files were aspirational"
> correction). We build a small, well-tested one. Scope: US/NANP defaults (this is a single personal
> US GV account); reject anything we cannot confidently normalize rather than guessing (ADR §4.2 #2:
> bare/ambiguous forms "will likely produce INVALID_ARGUMENT").

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/PhoneNumberNormalizerTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/PhoneNumberNormalizer.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class PhoneNumberNormalizerTests
{
    [Theory]
    [InlineData("+19195551234", "+19195551234")]   // already E.164
    [InlineData("9195551234", "+19195551234")]      // bare 10-digit NANP
    [InlineData("19195551234", "+19195551234")]     // 11-digit with country code
    [InlineData("(919) 555-1234", "+19195551234")]  // formatted
    [InlineData("919-555-1234", "+19195551234")]
    [InlineData(" 919.555.1234 ", "+19195551234")]  // punctuation + whitespace
    public void Normalize_ValidUsNumbers_ReturnsE164(string input, string expected)
    {
        Assert.True(PhoneNumberNormalizer.TryNormalize(input, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("12345")]            // too short
    [InlineData("555-1234")]         // 7-digit, no area code
    [InlineData("+44 20 7946 0958")] // non-NANP: out of scope v1, reject rather than guess
    [InlineData("abcdefghij")]       // non-numeric
    [InlineData("+1234567890123456")]// too long
    public void Normalize_InvalidOrUnsupported_ReturnsFalse(string? input)
    {
        Assert.False(PhoneNumberNormalizer.TryNormalize(input, out var result));
        Assert.Null(result);
    }
}
```

- [ ] **Step 2: Run → FAIL** (`PhoneNumberNormalizer` does not exist)
  `dotnet test src/RotaryPhoneController.GVBridge.Tests --filter "FullyQualifiedName~PhoneNumberNormalizerTests" -v n`

- [ ] **Step 3: Implement**

Create `src/RotaryPhoneController.GVBridge/Clients/PhoneNumberNormalizer.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Normalizes a user-typed phone number to E.164 for GV sendsms (ADR §4.2 #2). NANP/US scope only —
/// this is a single personal US GV account, and group/international threads are out of scope v1 (ADR §9).
/// Conservative by design: anything we cannot confidently map to a +1NXXNXXXXXX form is REJECTED
/// (returns false) rather than guessed, because a wrong number is an irreversible send to a stranger.
/// </summary>
public static class PhoneNumberNormalizer
{
    /// <summary>
    /// True + a +1NXXNXXXXXX string on success; false + null otherwise. Accepts +E.164, 10-digit NANP,
    /// or 11-digit (leading 1), with arbitrary punctuation/whitespace. Rejects empty, too-short,
    /// too-long, non-numeric, and non-+1 international forms.
    /// </summary>
    public static bool TryNormalize(string? input, out string? e164)
    {
        e164 = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        var hadPlus = trimmed.StartsWith('+');

        // Keep digits only.
        Span<char> buf = stackalloc char[trimmed.Length];
        var n = 0;
        foreach (var c in trimmed)
            if (char.IsDigit(c)) buf[n++] = c;
        var digits = new string(buf[..n]);

        if (digits.Length == 0) return false;

        // Explicit international (+ prefix and not +1...): out of scope v1 — reject, don't guess.
        if (hadPlus && !(digits.Length == 11 && digits[0] == '1'))
            return false;

        string tenDigits;
        if (digits.Length == 10)
            tenDigits = digits;                       // bare NANP
        else if (digits.Length == 11 && digits[0] == '1')
            tenDigits = digits[1..];                  // 1 + NANP
        else
            return false;                             // anything else is ambiguous → reject

        // NANP sanity: area code + exchange must start 2-9 (rough but catches obvious junk).
        if (tenDigits[0] is '0' or '1' || tenDigits[3] is '0' or '1')
            return false;

        e164 = "+1" + tenDigits;
        return true;
    }
}
```

- [ ] **Step 4: Run → PASS** (14 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/PhoneNumberNormalizer.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/PhoneNumberNormalizerTests.cs
git commit -m "feat(gv): add E.164 phone normalizer for SMS send (ADR §4.2 #2)"
```

---

## Task 3: SmsThreadIdResolver (the UNVERIFIED seam) — TDD (ADR §4.2 #1)

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/SmsThreadIdResolverTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/ISmsThreadIdResolver.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/SmsThreadIdResolver.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class SmsThreadIdResolverTests
{
    private readonly ISmsThreadIdResolver _resolver = new SmsThreadIdResolver();

    [Fact]
    public void Reply_WithExistingThreadId_UsesItVerbatim()
    {
        // ADR §4.2 #1: reply uses Google's real assigned id (may be an opaque t.<hash>).
        var id = _resolver.Resolve("+19195551234", explicitThreadId: "t.abc123hash");
        Assert.Equal("t.abc123hash", id);
    }

    [Fact]
    public void NewConversation_NullThreadId_SynthesizesTPlusE164()
    {
        // ADR §4.1 form for a NEW 1:1 thread. UNVERIFIED pending ADR §11 step 4.
        var id = _resolver.Resolve("+19195551234", explicitThreadId: null);
        Assert.Equal("t.+19195551234", id);
    }

    [Fact]
    public void NewConversation_EmptyOrWhitespaceThreadId_SynthesizesTPlusE164()
    {
        Assert.Equal("t.+19195551234", _resolver.Resolve("+19195551234", ""));
        Assert.Equal("t.+19195551234", _resolver.Resolve("+19195551234", "   "));
    }
}
```

- [ ] **Step 2: Run → FAIL**
- [ ] **Step 3: Implement the seam + default**

`src/RotaryPhoneController.GVBridge/Clients/ISmsThreadIdResolver.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Resolves the GV thread id to send to (ADR §4.2 #1). The reply-vs-new rule and the synthesized
/// new-thread format are UNVERIFIED (ADR §11 step 4) — isolating them here means the live-capture
/// correction is a ONE-FILE change, mirroring IGvThreadParser / GvThreadFolder.ToWireValue().
/// </summary>
public interface ISmsThreadIdResolver
{
    /// <summary>
    /// Given a normalized E.164 recipient and an optional existing thread id, return the id to send to.
    /// Non-empty explicitThreadId → reply (verbatim). Null/empty → new conversation (synthesized form).
    /// </summary>
    string Resolve(string normalizedE164, string? explicitThreadId);
}
```

`src/RotaryPhoneController.GVBridge/Clients/SmsThreadIdResolver.cs`:

```csharp
namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Default thread-id resolver. NEW-conversation form is "t.+<E164>" per ADR §4.1 — **UNVERIFIED**,
/// pending ADR §11 step 4 (send a test text, capture the id GV actually assigns, confirm reply uses
/// that id vs t.+<E164>). If live capture reveals a different new-thread format, fix it HERE only.
/// </summary>
public class SmsThreadIdResolver : ISmsThreadIdResolver
{
    public string Resolve(string normalizedE164, string? explicitThreadId)
        => string.IsNullOrWhiteSpace(explicitThreadId)
            ? $"t.{normalizedE164}"          // UNVERIFIED — ADR §11 step 4
            : explicitThreadId;              // reply: Google's real id, verbatim (ADR §4.2 #1)
}
```

- [ ] **Step 4: Run → PASS** (4 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/ISmsThreadIdResolver.cs \
        src/RotaryPhoneController.GVBridge/Clients/SmsThreadIdResolver.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/SmsThreadIdResolverTests.cs
git commit -m "feat(gv): add SMS thread-id resolver seam (reply vs new, ADR §4.2 #1)"
```

---

## Task 4: SmsSendRateLimiter (429 floor) — TDD (ADR §4.2 #4)

> **Why server-side, not just UI:** the handoff promises the UI a real "Sending too fast" path on
> **HTTP 429**, and the §4.2 #4 hazard is "a bug that loops it" — a client bug or a malicious LAN caller
> must not be able to spray sends. The authoritative guard lives on the server. Sliding 10s window.

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/SmsSendRateLimiterTests.cs`
- Create: `src/RotaryPhoneController.GVBridge/Clients/SmsSendRateLimiter.cs`

- [ ] **Step 1: Write failing tests** (inject a clock so tests are deterministic)

```csharp
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class SmsSendRateLimiterTests
{
    [Fact]
    public void AllowsUpToLimit_WithinWindow()
    {
        var now = DateTime.UtcNow;
        var limiter = new SmsSendRateLimiter(maxSends: 3, window: TimeSpan.FromSeconds(10), () => now);
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
        Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void RejectsOverLimit_WithinWindow()
    {
        var now = DateTime.UtcNow;
        var limiter = new SmsSendRateLimiter(maxSends: 3, window: TimeSpan.FromSeconds(10), () => now);
        limiter.TryAcquire(); limiter.TryAcquire(); limiter.TryAcquire();
        Assert.False(limiter.TryAcquire());   // 4th in the same window → reject (→ 429)
    }

    [Fact]
    public void AllowsAgain_AfterWindowSlides()
    {
        var now = DateTime.UtcNow;
        var clock = now;
        var limiter = new SmsSendRateLimiter(maxSends: 3, window: TimeSpan.FromSeconds(10), () => clock);
        limiter.TryAcquire(); limiter.TryAcquire(); limiter.TryAcquire();
        Assert.False(limiter.TryAcquire());
        clock = now.AddSeconds(11);            // window has passed
        Assert.True(limiter.TryAcquire());
    }
}
```

- [ ] **Step 2: Run → FAIL**
- [ ] **Step 3: Implement**

```csharp
using System.Collections.Concurrent;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Sliding-window send limiter (ADR §4.2 #4): rejects more than <paramref name="maxSends"/> sends per
/// window so a looping bug or hostile LAN caller cannot spray the owner's GV number. The controller
/// returns 429 when TryAcquire() is false — the UI's "Sending too fast" affordance (handoff) relies on
/// this being a real server response, not a UI-only guess. Process-wide (single GV account).
/// </summary>
public class SmsSendRateLimiter
{
    private readonly int _maxSends;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentQueue<DateTime> _stamps = new();
    private readonly object _gate = new();

    public SmsSendRateLimiter(int maxSends, TimeSpan window, Func<DateTime>? clock = null)
    {
        _maxSends = maxSends;
        _window = window;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True if a send is permitted now (and records it); false if the window is full.</summary>
    public bool TryAcquire()
    {
        var now = _clock();
        lock (_gate)
        {
            while (_stamps.TryPeek(out var oldest) && now - oldest >= _window)
                _stamps.TryDequeue(out _);

            if (_stamps.Count >= _maxSends) return false;
            _stamps.Enqueue(now);
            return true;
        }
    }
}
```

- [ ] **Step 4: Run → PASS** (3 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/SmsSendRateLimiter.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/SmsSendRateLimiterTests.cs
git commit -m "feat(gv): add server-side SMS send rate limiter (ADR §4.2 #4)"
```

---

## Task 5: GvSmsClient.SendAsync (the GV write) — TDD (ADR §4.1)

> Add ONLY `SendAsync` to the existing `GvSmsClient`. The read methods (`ListThreadsAsync`,
> `ListMessagesAsync`, `ListRecentMessagesAsync`) are untouched. `SendAsync` posts directly over the
> shared authenticated `HttpClient` (it cannot reuse `GvThreadClient.ListRawAsync`, which is list-only),
> so it takes the same `IGvAuthenticatedClientProvider` per-call resolution the read path uses — the
> write inherits cookie rotation + the recovery ladder for free (ADR §1.3, §7).

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Clients/GvSmsClientSendTests.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs`

- [ ] **Step 1: Write failing tests**

These exercise the test-facing path. We add a **test-facing `SendAsync` overload that takes an explicit
`HttpClient`** (mirroring `GvThreadClient`'s dual constructors) so the send can be tested hermetically
without the provider, while production resolves the live client per call.

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RotaryPhoneController.GVBridge.Clients;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Clients;

public class GvSmsClientSendTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";
    private const string ApiKey = "test-key";

    private static GvSmsClient ReadOnlyClient(HttpClient http)
    {
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, ApiKey, parser,
            NullLogger<GvThreadClient>.Instance);
        return new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
    }

    [Fact]
    public async Task SendAsync_PostsExpectedPayload_AndReturnsQueuedOn200()
    {
        string? capturedUrl = null;
        string? capturedBody = null;
        var http = new HttpClient(new CapturingHandler((req, body) =>
        {
            capturedUrl = req.RequestUri!.ToString();
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var client = ReadOnlyClient(http);

        var result = await client.SendAsync(http, "t.+19195551234", "hello world");

        Assert.True(result.Queued);
        Assert.Null(result.Error);
        Assert.Contains("api2thread/sendsms", capturedUrl);
        Assert.Contains("alt=protojson", capturedUrl);
        // ADR §4.1 payload shape: [null,null,null,null,"<text>","<threadId>"]
        using var doc = JsonDocument.Parse(capturedBody!);
        var arr = doc.RootElement;
        Assert.Equal(6, arr.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, arr[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, arr[3].ValueKind);
        Assert.Equal("hello world", arr[4].GetString());
        Assert.Equal("t.+19195551234", arr[5].GetString());
    }

    [Fact]
    public async Task SendAsync_NonSuccess_ReturnsNotQueuedWithError_NoThrow()
    {
        var http = new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("INVALID_ARGUMENT") }));
        var client = ReadOnlyClient(http);

        var result = await client.SendAsync(http, "t.+19195551234", "hi");

        Assert.False(result.Queued);                 // honest: a 400 is NOT queued
        Assert.NotNull(result.Error);
        Assert.Contains("400", result.Error);
    }

    [Fact]
    public async Task SendAsync_NullClient_ReturnsNotQueued_NoThrow()
    {
        var client = ReadOnlyClient(new HttpClient(new CapturingHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK))));
        var result = await client.SendAsync(authenticatedClient: null, "t.+19195551234", "hi");
        Assert.False(result.Queued);
        Assert.NotNull(result.Error);                // adapter down → honest failure, never a fake success
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, string?, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return handler(request, body);
        }
    }
}
```

- [ ] **Step 2: Run → FAIL** (`SendAsync` does not exist)

- [ ] **Step 3: Implement `SendAsync`**

Add to `src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs`. Add the using + a `GvSmsSendResult`
record, and the two `SendAsync` overloads (test-facing explicit client; production provider-resolved).
`GvSmsClient` currently only holds `GvThreadClient`/`IGvThreadParser`/`ILogger` — to resolve the live
client in production, **add an optional `IGvAuthenticatedClientProvider` constructor parameter** (DI
passes it; the existing read-only test constructor stays valid by leaving it null and using the
explicit-client overload).

```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RotaryPhoneController.GVBridge.Adapters;
using RotaryPhoneController.GVBridge.Protocol;

namespace RotaryPhoneController.GVBridge.Clients;

/// <summary>
/// Result of a GV sendsms call. Queued=true means Google ACCEPTED the request (HTTP 200) — NOT
/// confirmed delivery (ADR §4.2 #3, sendsms returns a transaction ack). Error is populated on any
/// non-200 / exception. Callers MUST NOT auto-retry on failure (ADR §4.2 #4).
/// </summary>
public record GvSmsSendResult(bool Queued, string? Error)
{
    public static GvSmsSendResult Ok() => new(true, null);
    public static GvSmsSendResult Fail(string error) => new(false, error);
}
```

Add the provider field + a constructor overload, and the send methods, to the `GvSmsClient` class:

```csharp
    // Optional — only needed for the write path (SendAsync). Null on the read-only test constructor.
    private readonly IGvAuthenticatedClientProvider? _provider;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    /// <summary>DI-facing constructor: adds the auth provider so SendAsync can resolve the live client.</summary>
    public GvSmsClient(GvThreadClient threadClient, IGvThreadParser parser,
        IGvAuthenticatedClientProvider provider, ILogger<GvSmsClient> logger)
        : this(threadClient, parser, logger)
    {
        _provider = provider;
        _baseUrl = provider.ApiBaseUrl.TrimEnd('/');
        _apiKey = provider.ApiKey;
    }

    /// <summary>
    /// Production send: resolves the live authenticated client per call (cookie rotation + recovery
    /// ladder, ADR §1.3) and posts api2thread/sendsms. threadId must already be resolved
    /// (ISmsThreadIdResolver) and the recipient already normalized (PhoneNumberNormalizer) by the caller.
    /// </summary>
    public Task<GvSmsSendResult> SendAsync(string threadId, string text, CancellationToken ct = default)
        => SendAsync(_provider?.GetAuthenticatedClient(), threadId, text, ct);

    /// <summary>
    /// Core send (test-facing overload takes an explicit client). Returns Queued only on HTTP 200;
    /// never throws — an exception or non-200 becomes Queued=false + Error (honest status, ADR §4.2 #3).
    /// Builds the EXACT ADR §4.1 payload [null,null,null,null,text,threadId] via GvProtobuf.BuildArray.
    /// </summary>
    public async Task<GvSmsSendResult> SendAsync(
        HttpClient? authenticatedClient, string threadId, string text, CancellationToken ct = default)
    {
        if (authenticatedClient is null)
        {
            _logger.LogWarning("sendsms skipped — authenticated client unavailable");
            return GvSmsSendResult.Fail("GV adapter unavailable (no authenticated client)");
        }
        try
        {
            var url = $"{_baseUrl}/api2thread/sendsms?alt=protojson&key={_apiKey}";
            // ADR §4.1 payload. The thread-id form is UNVERIFIED (ADR §11 step 4) — resolved upstream
            // by ISmsThreadIdResolver, passed in here verbatim.
            var payload = GvProtobuf.BuildArray(null, null, null, null, text, threadId);
            var content = new StringContent(payload, Encoding.UTF8, "application/json+protobuf");
            var response = await authenticatedClient.PostAsync(url, content, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("sendsms queued for thread {ThreadId}", threadId);
                return GvSmsSendResult.Ok();   // 200 = QUEUED, not delivered (honest)
            }
            _logger.LogWarning("sendsms returned {Status} for thread {ThreadId}",
                response.StatusCode, threadId);
            return GvSmsSendResult.Fail($"Google returned {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "sendsms failed for thread {ThreadId}", threadId);
            return GvSmsSendResult.Fail("Send request failed (network/exception)");
        }
    }
```

> Note: the test-facing read constructor `GvSmsClient(GvThreadClient, IGvThreadParser, ILogger)` stays.
> The send tests construct via that read constructor and call the **explicit-client** `SendAsync`
> overload — so `_baseUrl`/`_apiKey` are unset there. To keep the explicit overload usable in tests,
> initialize `_baseUrl`/`_apiKey` from the `GvThreadClient` is not possible (they're private); instead
> the explicit-client overload builds the URL from constants visible to it. **Concretely:** give
> `GvSmsClient` `private string _baseUrl = "https://clients6.google.com/voice/v1/voiceclient";` and
> `_apiKey = ""` defaults set in the read constructor, overwritten by the provider constructor. The
> test asserts on the path substring `api2thread/sendsms` + `alt=protojson` (not the host), so the
> default base URL is sufficient for the hermetic test. Implementer: keep both constructors consistent.

- [ ] **Step 4: Run → PASS** (3 cases)
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Clients/GvSmsClient.cs \
        src/RotaryPhoneController.GVBridge.Tests/Clients/GvSmsClientSendTests.cs
git commit -m "feat(gv): GvSmsClient.SendAsync (api2thread/sendsms, ADR §4.1) — queued != delivered"
```

---

## Task 6: GvSmsController POST /send + push decision — TDD (ADR §6.2)

> **Broadcast decision (recorded here, per the prompt):** on a successful send we **DO broadcast the
> outbound message** over `RotaryHub` as a **new `SmsSent` event** (payload `SmsMessageDto`,
> `Direction="Outbound"`). Rationale: a kiosk may have multiple viewers / a second tab; the §6.3 push
> model already makes RadioConsole push-shaped, and without this an outbound from one client is
> invisible to others until the next poll surfaces it. We use a **distinct `SmsSent` event** (not
> `SmsReceived`) so RadioConsole can append it without a "new inbound" toast (handoff: outbound is the
> user's own action — no toast). The handler is added to `GvMessagePushBridge` for symmetry, but the
> controller raises it directly via `IHubContext` is NOT possible from GVBridge (no SignalR dep) — so
> the broadcast is wired through a tiny seam, mirroring `IGvMessageEventSource`. **See Task 6 Step 4.**

**Files:**
- Create: `src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerSendTests.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs`
- Modify: `src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs` (add `OnSmsSent`)
- Modify: `src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs` (forward `SmsSent`)

- [ ] **Step 1: Extend the event seam with an outbound channel**

Add to `src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs`:

```csharp
    /// <summary>
    /// Raised once per successfully-queued OUTBOUND SMS (the send the user just made). Distinct from
    /// OnSmsReceived so RadioConsole appends it WITHOUT an inbound toast (handoff). Forwarded to
    /// RotaryHub as "SmsSent".
    /// </summary>
    event Action<SmsMessageDto>? OnSmsSent;
```

And implement it on `GvThreadPoller` (which is the registered `IGvMessageEventSource`): add the event
plus a **public raise method** the controller can call, since the controller produces outbound sends
(the poller does not):

```csharp
    public event Action<SmsMessageDto>? OnSmsSent;

    /// <summary>Invoked by GvSmsController after a successful send so the outbound echo reaches RadioConsole.</summary>
    public void RaiseSmsSent(SmsMessageDto dto) => OnSmsSent?.Invoke(dto);
```

> The controller depends on `IGvMessageEventSource` only to call `RaiseSmsSent`. To avoid widening the
> seam with a producer method, add a **narrow producer interface** `IGvOutboundSmsSink` with
> `void NotifySent(SmsMessageDto dto)` implemented by `GvThreadPoller` (it already owns the events),
> and inject THAT into the controller. Keeps `IGvMessageEventSource` consumer-only. Implementer: add
> `IGvOutboundSmsSink` next to `IGvMessageEventSource`; register `GvThreadPoller` as both.

- [ ] **Step 2: Write failing controller tests**

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RotaryPhoneController.GVBridge.Api;
using RotaryPhoneController.GVBridge.Clients;
using RotaryPhoneController.GVBridge.Models;
using RotaryPhoneController.GVBridge.Services;
using Xunit;

namespace RotaryPhoneController.GVBridge.Tests.Api;

public class GvSmsControllerSendTests
{
    private const string BaseUrl = "https://clients6.google.com/voice/v1/voiceclient";

    private static (GvSmsController c, List<SmsMessageDto> sent) NewController(
        Func<HttpRequestMessage, HttpResponseMessage> handler, int maxSends = 3)
    {
        var http = new HttpClient(new MockHandler(handler));
        var parser = new PositionalGvThreadParser();
        var threadClient = new GvThreadClient(http, BaseUrl, "k", parser, NullLogger<GvThreadClient>.Instance);
        var smsClient = new GvSmsClient(threadClient, parser, NullLogger<GvSmsClient>.Instance);
        var limiter = new SmsSendRateLimiter(maxSends, TimeSpan.FromSeconds(10));
        var resolver = new SmsThreadIdResolver();
        var sentSink = new List<SmsMessageDto>();
        var sink = new TestSink(sentSink);
        var controller = new GvSmsController(smsClient, limiter, resolver, sink,
            NullLogger<GvSmsController>.Instance);
        // Inject the same http as the "authenticated client" for the write path test seam:
        controller.SetSendClientForTest(http);
        return (controller, sentSink);
    }

    private static HttpResponseMessage Ok200() => new(HttpStatusCode.OK) { Content = new StringContent("[]") };

    [Fact]
    public async Task Send_NewConversation_NormalizesAndReturnsOutboundEcho()
    {
        var (controller, sent) = NewController(_ => Ok200());
        var result = await controller.Send(new SendSmsRequest("(919) 555-1234", "hi there", null), default);
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<SendSmsResponse>(ok.Value);
        Assert.True(resp.Queued);
        Assert.Equal("t.+19195551234", resp.ThreadId);
        Assert.NotNull(resp.Message);
        Assert.Equal("Outbound", resp.Message!.Direction);
        Assert.Equal("hi there", resp.Message.Text);
        Assert.Single(sent);                         // broadcast over the sink
    }

    [Fact]
    public async Task Send_InvalidNumber_Returns400_NoSend()
    {
        var (controller, sent) = NewController(_ => Ok200());
        var result = await controller.Send(new SendSmsRequest("not-a-number", "hi", null), default);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(sent);                          // never reached Google
    }

    [Fact]
    public async Task Send_EmptyText_Returns400()
    {
        var (controller, _) = NewController(_ => Ok200());
        var result = await controller.Send(new SendSmsRequest("9195551234", "   ", null), default);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Send_OverRateLimit_Returns429()
    {
        var (controller, _) = NewController(_ => Ok200(), maxSends: 1);
        await controller.Send(new SendSmsRequest("9195551234", "one", null), default);
        var second = await controller.Send(new SendSmsRequest("9195551234", "two", null), default);
        var status = Assert.IsType<ObjectResult>(second);
        Assert.Equal(429, status.StatusCode);
    }

    [Fact]
    public async Task Send_GoogleRejects_Returns502_QueuedFalse_NoBroadcast()
    {
        var (controller, sent) = NewController(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad") });
        var result = await controller.Send(new SendSmsRequest("9195551234", "hi", null), default);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        Assert.Empty(sent);                          // no fake "sent" echo on failure (honest status)
    }

    private sealed class TestSink(List<SmsMessageDto> captured) : IGvOutboundSmsSink
    {
        public void NotifySent(SmsMessageDto dto) => captured.Add(dto);
    }

    private sealed class MockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
```

- [ ] **Step 3: Run → FAIL**

- [ ] **Step 4: Implement the POST /send endpoint**

Add to `src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs`. The controller orchestrates the
§4.2 rules in order: **rate-limit → normalize → validate text → resolve thread id → send → honest
map → broadcast on success.** It needs the rate limiter, the resolver, the outbound sink, and a way to
get the authenticated client for the send (via the `GvSmsClient` provider-backed `SendAsync(threadId,
text)` in production; a test seam for hermetic tests).

```csharp
// add to the constructor + fields
private readonly SmsSendRateLimiter _rateLimiter;
private readonly ISmsThreadIdResolver _threadIdResolver;
private readonly IGvOutboundSmsSink _outboundSink;
private HttpClient? _testSendClient;   // test-only; null in production (uses GvSmsClient.SendAsync(threadId,text))

public GvSmsController(GvSmsClient smsClient, SmsSendRateLimiter rateLimiter,
    ISmsThreadIdResolver threadIdResolver, IGvOutboundSmsSink outboundSink,
    ILogger<GvSmsController> logger)
{
    _smsClient = smsClient;
    _rateLimiter = rateLimiter;
    _threadIdResolver = threadIdResolver;
    _outboundSink = outboundSink;
    _logger = logger;
}

/// <summary>Test seam: inject the HttpClient used as the "authenticated client" for the write path.</summary>
internal void SetSendClientForTest(HttpClient client) => _testSendClient = client;

[HttpPost("send")]
public async Task<IActionResult> Send([FromBody] SendSmsRequest request, CancellationToken ct = default)
{
    // 1. Rate-limit FIRST (ADR §4.2 #4) — cheap, and a real 429 backs the UI's "Sending too fast".
    if (!_rateLimiter.TryAcquire())
    {
        _logger.LogWarning("SMS send rejected by rate limiter");
        return StatusCode(429, new SendSmsResponse(
            Queued: false, ThreadId: null, Error: "Sending too fast — wait a moment", Message: null));
    }

    // 2. Validate text.
    if (string.IsNullOrWhiteSpace(request.Text))
        return BadRequest(new SendSmsResponse(false, null, "Message text is required", null));

    // 3. Normalize the recipient to E.164 (ADR §4.2 #2). Reject ambiguous numbers — never guess.
    if (!PhoneNumberNormalizer.TryNormalize(request.ToNumber, out var e164) || e164 is null)
        return BadRequest(new SendSmsResponse(
            false, null, $"Invalid or unsupported number: {request.ToNumber}", null));

    // 4. Resolve the thread id (ADR §4.2 #1): reply id verbatim, else synthesized t.+<E164> (UNVERIFIED).
    var threadId = _threadIdResolver.Resolve(e164, request.ThreadId);

    // 5. Send. In production GvSmsClient resolves the live authenticated client per call; the test seam
    //    injects an explicit client.
    var sendResult = _testSendClient is not null
        ? await _smsClient.SendAsync(_testSendClient, threadId, request.Text, ct)
        : await _smsClient.SendAsync(threadId, request.Text, ct);

    // 6. Honest mapping (ADR §4.2 #3): NO auto-retry. 200=queued; anything else = 502, Queued=false.
    if (!sendResult.Queued)
        return StatusCode(502, new SendSmsResponse(false, threadId, sendResult.Error, null));

    // 7. Build the optimistic OUTBOUND echo (NOT a parse of Google's ack — it returns no message).
    var echo = new SmsMessageDto(
        Id: $"local-{Guid.NewGuid():N}",          // local placeholder; poller will surface the real id
        ThreadId: threadId,
        Direction: "Outbound",
        CounterpartyNumber: e164,
        Text: request.Text,
        SentAt: DateTime.UtcNow,
        IsRead: true);

    // 8. Broadcast so other connected clients converge (decision in Task 6 header). Distinct SmsSent event.
    _outboundSink.NotifySent(echo);

    return Ok(new SendSmsResponse(Queued: true, ThreadId: threadId, Error: null, Message: echo));
}
```

> **PR5 interaction (no rework when the gate ships):** this endpoint lives under the `/api/gvbridge/*`
> prefix that PR5's middleware gates wholesale. It needs **no per-endpoint auth attribute** — PR5 is a
> pipeline middleware, so enabling the gate later requires **zero changes here**. Do not add ad-hoc auth
> in this PR; that is PR5's single, uniform mechanism (ADR §6.5).

- [ ] **Step 5: Wire the Server-side `SmsSent` broadcast**

In `src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs`, subscribe to `OnSmsSent` and
broadcast `"SmsSent"` (mirroring the existing `SmsReceived`/`VoicemailReceived` handlers):

```csharp
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _eventSource.OnSmsReceived += BroadcastSms;
        _eventSource.OnVoicemailReceived += BroadcastVoicemail;
        _eventSource.OnSmsSent += BroadcastSmsSent;            // NEW
        return Task.CompletedTask;
    }
    // ... StopAsync: unsubscribe OnSmsSent too ...

    private void BroadcastSmsSent(GVBridge.Api.SmsMessageDto dto)
    {
        _logger.LogInformation("Broadcasting SmsSent {Id} to {Number}", dto.Id, dto.CounterpartyNumber);
        _ = _hubContext.Clients.All.SendAsync("SmsSent", dto);
    }
```

- [ ] **Step 6: Run → PASS** (5 cases) and build the Server project.
- [ ] **Step 7: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Api/GvSmsController.cs \
        src/RotaryPhoneController.GVBridge/Services/IGvMessageEventSource.cs \
        src/RotaryPhoneController.GVBridge/Services/GvThreadPoller.cs \
        src/RotaryPhoneController.Server/Services/GvMessagePushBridge.cs \
        src/RotaryPhoneController.GVBridge.Tests/Api/GvSmsControllerSendTests.cs
git commit -m "feat(gv): POST /api/gvbridge/sms/send with E.164/rate-limit/honest-status + SmsSent push"
```

---

## Task 7: Config + DI wiring

**Files:**
- Modify: `src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs`
- Modify: `src/RotaryPhoneController.Server/appsettings.json`
- Modify: `src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs`

- [ ] **Step 1: Add rate-limit config** to `GVBridgeConfig` (after the poller block):

```csharp
    // SMS send rate limit (ADR §4.2 #4). Reject more than N sends per window → HTTP 429. Owner-tunable;
    // conservative defaults for a single personal account. (PR4 — owner-hold capability.)
    public int SmsSendMaxPerWindow { get; set; } = 5;
    public int SmsSendWindowSeconds { get; set; } = 10;
```

- [ ] **Step 2: Add the keys to `appsettings.json`** `GVBridge` section:

```json
"SmsSendMaxPerWindow": 5,
"SmsSendWindowSeconds": 10
```

- [ ] **Step 3: Register the new services** in `AddGVBridge` (after the existing `GvSmsClient` block).
Update the `GvSmsClient` registration to the **provider-backed constructor** so production `SendAsync`
can resolve the live client:

```csharp
        // SMS send (PR4 — owner-hold). Provider-backed GvSmsClient so SendAsync resolves the live
        // authenticated client per call (cookie rotation + recovery ladder, ADR §1.3, §7).
        services.AddSingleton<GvSmsClient>(sp => new GvSmsClient(
            sp.GetRequiredService<GvThreadClient>(),
            sp.GetRequiredService<IGvThreadParser>(),
            sp.GetRequiredService<IGvAuthenticatedClientProvider>(),
            sp.GetRequiredService<ILogger<GvSmsClient>>()));

        services.AddSingleton<ISmsThreadIdResolver, SmsThreadIdResolver>();
        services.AddSingleton<IGvOutboundSmsSink>(sp => sp.GetRequiredService<GvThreadPoller>());
        services.AddSingleton<SmsSendRateLimiter>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<GVBridgeConfig>>().Value;
            return new SmsSendRateLimiter(cfg.SmsSendMaxPerWindow,
                TimeSpan.FromSeconds(cfg.SmsSendWindowSeconds));
        });
```

> Add `using Microsoft.Extensions.Options;` to the extensions file if not present. The existing
> `GvSmsClient` registration (3-arg) is REPLACED by the 4-arg one above — confirm only one registration
> remains.

- [ ] **Step 4: Build the Server project** → Build succeeded.
- [ ] **Step 5: Commit**

```bash
git add src/RotaryPhoneController.GVBridge/Models/GVBridgeConfig.cs \
        src/RotaryPhoneController.Server/appsettings.json \
        src/RotaryPhoneController.GVBridge/Extensions/GVBridgeServiceExtensions.cs
git commit -m "feat(gv): wire SMS send services + rate-limit config (PR4)"
```

---

## Task 8: Full suite + completion gate

- [ ] **Step 1: Full GVBridge test suite** — `dotnet test src/RotaryPhoneController.GVBridge.Tests -v n` → all green
  (read-side 151 tests + the new send tests).
- [ ] **Step 2: Server build** — `dotnet build src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj` → succeeded.
- [ ] **Step 3: No browser UAT here.** As with PR1–3: the RadioConsole UI lives in the **RTest repo**
  (memory `project_ui_integration.md`); there is no in-repo browser flow. Backend gates = unit/fixture
  suite + the live capture below.
- [ ] **Step 4: Live verification (owner/Tester on the `radio` box) — ADR §11 step 4.** ONLY after the
  owner green-lights the build:
  - `POST /api/gvbridge/sms/send` `{ "toNumber": "+1<TEST>", "text": "test" }` to a **known test number
    you own** → confirm HTTP 200, `queued:true`, and the message **actually arrives**.
  - Capture the thread id GV assigned; send a **reply** with that id vs `t.+<E164>` → **pin the reply
    rule** and de-UNVERIFY `SmsThreadIdResolver` / `t.+<E164>` (one-file fix if wrong).
  - Confirm a 429 fires when you exceed the configured rate (e.g. 6 rapid sends with default 5/10s).
  - Confirm a deliberately bad number returns 400 and **never reaches Google**.

---

## Out of scope for PR4 (do NOT do here)

- **No inter-service auth gate** — that is PR5. This endpoint is *designed* to sit behind PR5's
  `/api/gvbridge/*` middleware with zero rework, but PR4 adds **no** auth attribute/header logic.
- **No per-send confirmation dialog** — that is a RadioConsole UI concern (ADR §12 #1, flagged below).
- **No outbound MMS/media, no group threads** (ADR §9, out of scope v1).
- **No mark-read/delete** (ADR §3.4, out of scope v1).
- **No retry logic of any kind** (ADR §4.2 #4 — explicitly forbidden; retry is the UI's user-driven choice).
- **No parsing of the `sendsms` ack body** for the message (ADR §4.2 #3 — it isn't the message; the
  poller surfaces the authoritative copy).

---

## ADR §12 decision points flagged for the owner (do not block — plan against defaults)

1. **§12 #1 — per-send confirmation in the UI.** This plan ships send **behind the rate-limit + (later)
   the PR5 gate**, the ADR's default. **If the owner wants a per-send "Are you sure?" confirm for v1,**
   that is a **RadioConsole-side** change (the handoff already builds a flagged compose UI) — no
   RotaryPhone change needed; flag it to the RadioConsole track when send ships.
2. **§12 #1 — autonomy.** Plan assumes send ships gated + rate-limited (default). No change needed if
   the owner confirms; if the owner wants it disabled-by-default, add an `EnableSmsSend` flag (trivial,
   not built here pending that decision).
3. **Default rate limit (5 / 10s).** Owner-tunable via config. **If the owner wants a stricter floor**
   (e.g. 2/10s), change the appsettings values only — no code change.
