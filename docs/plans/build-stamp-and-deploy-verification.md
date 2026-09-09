# Build stamp and deploy verification — plan

**Branch:** `feat/build-stamp-and-deploy-verification` — cut fresh from `main`
**Origin:** Radio Console request, `docs/prompts/radioconsole-cdp-spam-and-build-stamp-request.md`
§2 (2026-07-31), restated as item 2 of `D:/prj/RTest/RTest/docs/queue/CROSS-REPO-HANDOFFS.md`.
Their side shipped the equivalent as `OPS-1` (PR #485, 2026-09-01).
**Baseline verified against:** `main` @ `3c2c892` (*Merge pull request #76 from
mmackelprang/fix/gv-voicemail-blackout-404*).
**Box state at time of writing:** `radio` is running `738141f` (PR #72, 2026-08-01) — **23 commits
and 4 merged PRs behind `main`**, and nothing on the box says so.

---

## 0. Headline — the stamp already exists. Nobody can read it. That is the whole bug.

**Every assembly on the box already carries its git SHA.** Measured 2026-09-08 against the live
box, all four:

```
RotaryPhoneController.Core.dll      1.0.0+738141f81f962ed6a0d0794f16b34f36685d2e17
RotaryPhoneController.GVBridge.dll  1.0.0+738141f81f962ed6a0d0794f16b34f36685d2e17
RotaryPhoneController.GVTrunk.dll   1.0.0+738141f81f962ed6a0d0794f16b34f36685d2e17
RotaryPhoneController.Server.dll    1.0.0+738141f81f962ed6a0d0794f16b34f36685d2e17
```

This repo has **no** `Directory.Build.props`, **no** SourceLink `PackageReference`, and **zero**
occurrences of `SourceRevisionId` anywhere in the tree. The stamp is implicit .NET SDK behaviour —
the SDK's bundled SourceLink sets `SourceRevisionId` from git and the SDK appends it to
`AssemblyInformationalVersion`. **Verified empirically**, not assumed: a bare `Microsoft.NET.Sdk`
class library with a two-line `.csproj`, built inside a fresh `git init` repo, produced
`1.0.0+913e66a98c96628994709f2b04fcea410e680035` — the exact HEAD SHA.

⚠ **So Task 1 is not "add stamping". It is "stop relying on luck."** Two failure modes were
measured on the same scratch project:

| Condition | Result | Consequence |
|---|---|---|
| Built inside a git work tree | `1.0.0+<sha>` | works today, by accident |
| **Same sources, `.git` absent** | **`1.0.0`** — no `+`, no SHA, **no warning** | the stamp vanishes silently |
| `-p:SourceRevisionId=deadbeef…` passed explicitly | `1.0.0+deadbeef…` | the deploy *can* bake an authoritative value |

The third row is what makes deploy verification possible at all. The second is why the behaviour
must be pinned rather than inherited.

**What is genuinely missing is everything downstream of the stamp:** no endpoint exposes it, no
deploy step checks it, and nothing detects a dirty working tree. That is why this session had to
`ssh` in and run `strings` against a DLL to answer *"is the XR-2 fix running?"* — and got a **false
negative on the first attempt**, because .NET stores those literals as UTF-16 in the `#US` heap and
plain ASCII `strings` cannot see them. `strings -el` was needed. A wrong *"the fix is not deployed"*
nearly went into a cross-repo report.

### The cost already paid, in one paragraph

`CROSS-REPO-HANDOFFS.md:11` records, struck through and marked **✅ SETTLED 2026-07-31**, that *"the
deployed tree is `D:\prj\rp-deploy` @ `0a86898`, NOT `D:\prj\RotaryPhone`"* — and blocked Radio
Console's `GV-5` on it pending re-derivation of ADR-028. **The deployed binary falsifies both
halves of that claim.** `0a86898` is PR #68 (2026-07-29); the box is running `738141f`, PR #72
(2026-08-01), a commit on this repo's `main`. `rp-deploy` is an orphaned worktree of *this same
repository*, frozen at PR #68 — not a separate tree, and not what runs. A confidently-wrong
conclusion, marked settled, blocked a partner team's work for six weeks. **One HTTP call would have
answered it correctly on day one, and the data needed to answer it was already inside the binary.**

That is the argument for this feature, and it is also the argument for §4 Task 7: make the
authoritative-tree question self-answering, so it cannot be got wrong again.

---

## 1. Investigation verification

Radio Console's request is five weeks old. Re-verified against `main` @ `3c2c892` and against the
live box on 2026-09-08.

| Claim | Status | Evidence |
|---|---|---|
| 2026-07-29: stale `rotary-phone` binary after a deploy restarted only `radio-api`/`radio-web` | **Confirmed as still possible** | `deploy/Deploy-ToLinux.ps1:221` is the *only* `systemctl restart` in this repo, and it restarts only `rotary-phone.service`. Neither side's deploy touches the other's units, and neither verifies. |
| `Directory.Build.props` should stamp `SourceRevisionId` | **Confirmed as missing — but the stamp works anyway** | No `Directory.Build.props`/`.targets`/`global.json` exists. See §0. |
| Deploy should pass `-p:SourceRevisionId=<sha>` | **Confirmed missing** | `Deploy-ToLinux.ps1:54-63` `$publishArgs` has no such flag. |
| A `/version` endpoint should exist | **Confirmed missing** | Zero hits for `/version`, `MapHealthChecks`, `AddHealthChecks` anywhere in `src/`. |
| Deploy should verify and fail loudly | **Confirmed missing** | The only post-restart action is `systemctl status … \| Write-Host` at `:223`, **whose exit code is never checked**; `:227` then unconditionally prints `=== Deploy Complete ===`. A restart-looping or stale service prints success. |
| *"`Assembly.Location` is empty for `PublishSingleFile`"* (their gotcha) | **Does not apply here** | `Deploy-ToLinux.ps1:54-63` passes `--self-contained` **without** `PublishSingleFile`; `grep` for `PublishSingleFile\|SelfContained\|RuntimeIdentifier` across all `*.csproj/*.props/*.targets` returns zero. The box confirms it: loose DLLs beside a 78 KB apphost, `runtimeconfig.json` shows `includedFrameworks`. Their *"~130 MB single-file bundle"* describes **their** artifact, not ours. We keep the fallback anyway — one line, and it stops a silent `DateTime.MinValue` if that ever changes. |
| *"the deployed tree is `rp-deploy` @ `0a86898`"* | **FALSIFIED** | See §0. Deployed assemblies report `738141f`, a commit on this repo's `main`. |
| `/api/gvbridge/status` is reachable and anonymous | **Confirmed** | `curl http://localhost:5004/api/gvbridge/status` → HTTP 200 with no auth header. |
| Radio Console's endpoint shape | **Confirmed live, not from source** | `curl radio:5000/api/health/version` and `:5002` both return the exact six fields — see §3.1. |

### Facts established on the box, 2026-09-08

- Production port is **5004** (`deploy/rotary-phone.service:12`, `ASPNETCORE_URLS`), not 5555 (dev,
  `launchSettings.json:8`). ⚠ `docs/plans/gv-crossrepo-xr2-verify-and-xr6-blackout-404.md:683-686`
  asserts the opposite; it is wrong for the deployed service. Verification must poll 5004.
- The service binds `0.0.0.0:5004`, so Radio Console can reach the endpoint at
  `http://192.168.86.50:5004` **without ssh**, which is requirement 4.
- `MainPID=2045`, `ActiveEnterTimestamp=2026-09-06 03:01:29`, `NRestarts=0`; deployed DLL mtime
  `2026-08-01 19:44:56`. The running process *does* currently match its own on-disk files.

---

## 2. Two mechanisms this plan depends on

### 2.1 Why the endpoint must report LOADED assemblies, not files on disk

The 2026-07-29 incident is *files updated, process not restarted*. On-disk metadata would report
the **new** SHA and declare everything fine — it would hide precisely the failure it exists to
catch. Reading the loaded assemblies reports what the process is actually executing, so a missed
restart shows up as a mismatch against the SHA the deploy just built.

`Assembly.Load(AssemblyName.GetAssemblyName(path))` resolves by **identity**. When an assembly of
that identity is already loaded it returns the loaded instance and does not re-read the newer file.
That is the behaviour we want, and it is why forcing the load is safe.

### 2.2 Why one SHA is not enough here, and is enough for Radio Console

Radio Console runs **two processes**, `radio-api` and `radio-web`, each a separate publish. One SHA
per endpoint is a complete answer for them, and they verify the two independently.

RotaryPhone ships **four assemblies into one process**. A single SHA cannot express *"GVBridge is
current but GVTrunk is stale"* — and partial staleness is the exact shape of the incident. So the
payload carries a per-assembly list plus a `consistent` boolean. **This is the one place the design
deliberately diverges from `OPS-1`, and it diverges because the topology differs, not because their
design is wrong.**

---

## 3. Design decisions

### 3.1 Path: `/api/health/version`, not `/version`

Radio Console serves `/api/health/version` on both services. Verified live 2026-09-08:

```
$ curl -s http://localhost:5000/api/health/version
{"gitSha":"c61f92760609581776579039452ca58b52ffad3e","gitShaShort":"c61f927",
 "informationalVersion":"1.0.0+c61f927...","assemblyVersion":"1.0.0.0",
 "buildTimestampUtc":"2026-09-05T10:05:20.1280976Z","assemblyName":"Radio.API"}
```

**Decision: mirror the path exactly.** The stated reason for mirroring at all is that a human
comparing two boxes should not have to learn two formats — and after this ships there will be
*three* endpoints on one box (`:5000`, `:5002`, `:5004`). One path, one shape, three ports is the
whole prize; a bare `/version` on ours would forfeit it for no gain.

**Considered and rejected:** adding `/version` as an alias. It doubles the surface a human must
learn, which is the opposite of the goal, and `Program.cs:418` `MapFallbackToFile("index.html")`
makes bare top-level routes a namespace hazard. If the owner prefers `/version`, say so and it
becomes a one-line `[HttpGet]` addition — but it should not be the canonical path.

### 3.2 Host: `RotaryPhoneController.Server`

There is one host process; it is the only candidate. Implemented as a `HealthController` with
`[Route("api/[controller]")]` + `[HttpGet("version")]`, which resolves to `api/health/version` and
mirrors Radio Console's `HealthController` structurally. This repo is **controllers-only** — `grep`
for `MapGet`/`MapPost` across `src/` returns zero hits — and `MapControllers()` auto-discovers it,
so `Program.cs` needs **no change**.

### 3.3 Auth: anonymous, deliberately, with a regression test pinning it

`GvBridgeAuthMiddleware` gates **only** paths starting `/api/gvbridge`. A route at
`/api/health/version` is therefore ungated **with no code change**. That is the right outcome, for
three reasons:

> ⚠ **Updated 2026-09-09.** This paragraph previously added "(exempting `/api/gvbridge/event`)". That
> exemption has been removed — the gate now covers every `/api/gvbridge/*` path with no exceptions.
> The reasoning below is unaffected: `/api/health/version` is outside the `/api/gvbridge` prefix, so
> it stays ungated for the reasons given, not because of any carve-out.

1. The endpoint's job is to be read by a deploy script and by Radio Console **without
   credentials**. Requiring the header would put the deploy check behind the same secret whose
   misconfiguration you most need the endpoint to help you diagnose — it would fail *closed* in the
   one case it matters.
2. The box is LAN-only (`192.168.86.50`), and `GVBridge:InterServiceAuthKey` is `""` today, so the
   gate is off across the board anyway.
3. The disclosure is a 40-char SHA of a private repo. Real, but small, and `/api/gvbridge/status`
   already exposes more operational detail anonymously.

⚠ **The risk is drift, not today's state.** If someone later broadens the middleware prefix, the
deploy check breaks in a confusing way. Task 5 adds an `[InlineData]` case to the existing
`GvBridgeAuthMiddlewareTests` asserting this route passes **with the gate on** — that test is the
thing that keeps the decision true.

### 3.4 Payload: their six fields verbatim, then four of ours

```json
{
  "gitSha": "3c2c892044f29dc5c454fc3533f5284b7bbaac12",
  "gitShaShort": "3c2c892",
  "informationalVersion": "1.0.0+3c2c892044f29dc5c454fc3533f5284b7bbaac12",
  "assemblyVersion": "1.0.0.0",
  "buildTimestampUtc": "2026-09-08T20:14:03.1234567Z",
  "assemblyName": "RotaryPhoneController.Server",
  "isDirty": false,
  "consistent": true,
  "processStartedAtUtc": "2026-09-08T20:14:09.0000000Z",
  "assemblies": [
    { "assemblyName": "RotaryPhoneController.Core",     "gitSha": "3c2c892…", "gitShaShort": "3c2c892", "informationalVersion": "1.0.0+3c2c892…", "assemblyVersion": "1.0.0.0", "buildTimestampUtc": "…", "isDirty": false },
    { "assemblyName": "RotaryPhoneController.GVBridge", "…": "…" },
    { "assemblyName": "RotaryPhoneController.GVTrunk",  "…": "…" },
    { "assemblyName": "RotaryPhoneController.Server",   "…": "…" }
  ]
}
```

**The first six fields are byte-for-byte their contract, in their order**, so a diff of two
responses lines up. Each addition earns its place:

| Field | Why |
|---|---|
| `assemblies[]` | §2.2 — four assemblies, one process, independently stale. |
| `consistent` | The one-glance answer to §2.2, and what the deploy asserts. |
| `isDirty` | Radio Console does not detect a dirty tree at all (confirmed: zero hits for `dirty`/`--porcelain` in their chain). We deploy by hand from a dev box with **no CI**, so building from a dirty tree is routine here, not exotic. Without it a hand-edited build is indistinguishable from the commit it claims to be. |
| `processStartedAtUtc` | Separates *"stale binary"* from *"binary is current, process is old"* at a glance, which is the incident's two halves. |

`buildTimestampUtc` is **file mtime — landed-on-disk time, not compile time**, exactly as Radio
Console documents. ⚠ It is *weaker* here than there: `Deploy-ToLinux.ps1` has two sync paths
(rsync at `:85-101`, a tar-pipe fallback at `:103-139` using `--unlink-first`) that do not preserve
mtime identically. **The SHA is the authoritative signal; the timestamp is a hint.**

---

## 4. Task list

### Task 0 — Branch from `main`

```bash
cd /mnt/d/prj/rotaryphone
git switch main && git pull --ff-only
git switch -c feat/build-stamp-and-deploy-verification
```

**Verify before proceeding — all four must hold, or stop:**

```bash
git rev-parse HEAD                      # expect 3c2c892... (or newer main)
test ! -f Directory.Build.props && echo "OK: no Directory.Build.props"
git grep -c "SourceRevisionId" -- '*.csproj' '*.props' '*.ps1' || echo "OK: zero hits"
git grep -c "api/health" -- 'src/' || echo "OK: zero hits"
```

⚠ **Do not read source from the working tree without checking the branch first.** A prior session
on `diag/gv-srtp-receive` (51+ commits behind) read stale controllers and drew wrong conclusions
from them. If in doubt: `git show main:<path>`.

---

### Task 1 — `Directory.Build.props`: pin the stamp, add dirty detection

**Create** `/mnt/d/prj/rotaryphone/Directory.Build.props` (new file — nothing to merge; the repo
has no `Directory.Build.props`, `.targets`, `Directory.Packages.props`, `global.json`, or
`.editorconfig`):

```xml
<Project>

  <PropertyGroup>
    <!-- Pin the SDK behaviour we depend on rather than inheriting it. Measured 2026-09-08: the
         SDK's bundled SourceLink already sets SourceRevisionId from git, which is why every
         assembly on the box carries "1.0.0+<sha>" despite this repo never asking for it. That is
         luck, not design - the same sources built outside a git work tree produce a bare "1.0.0"
         with no '+' and no warning. Stating this explicitly means a future SDK default cannot
         quietly remove the stamp that deploy verification depends on. -->
    <IncludeSourceRevisionInInformationalVersion>true</IncludeSourceRevisionInInformationalVersion>
  </PropertyGroup>

  <!--
    Stamp the git revision into AssemblyInformationalVersion so /api/health/version - and the
    deploy check that reads it - can prove which commit a running binary was built from.

    Deploy-ToLinux.ps1 passes -p:SourceRevisionId=<value> explicitly and is authoritative; this
    target is the fallback for local builds only and never overrides an explicit value (that is
    what the Condition is for). Mirrors Radio Console's _StampGitCommitHash
    (RTest/Directory.Build.props:23-46) - same target name, same Exec flags, deliberately, so the
    two repos stay diffable. The "-dirty" suffix is ours; they have no equivalent.
  -->
  <Target Name="_StampGitCommitHash"
          BeforeTargets="GetAssemblyVersion;GenerateNuspec;CoreCompile"
          Condition="'$(SourceRevisionId)' == ''">

    <Exec Command="git rev-parse HEAD"
          ConsoleToMSBuild="true"
          IgnoreExitCode="true"
          EchoOff="true"
          StandardOutputImportance="Low"
          StandardErrorImportance="Low">
      <Output TaskParameter="ConsoleOutput" PropertyName="_GitCommitHash" />
      <Output TaskParameter="ExitCode" PropertyName="_GitExitCode" />
    </Exec>

    <!-- A dirty tree stamps the same SHA as a clean one, so without this a hand-edited local
         build is indistinguishable from the commit it names. Build outputs are gitignored
         (.gitignore:24 bin/, :25 obj/, :93 publish/), so a deploy's own publish step cannot
         dirty the tree and trip this. Untracked files DO count as dirty, deliberately: a new
         .cs file that is not yet committed changes what gets compiled. -->
    <Exec Command="git status --porcelain"
          ConsoleToMSBuild="true"
          IgnoreExitCode="true"
          EchoOff="true"
          StandardOutputImportance="Low"
          StandardErrorImportance="Low">
      <Output TaskParameter="ConsoleOutput" PropertyName="_GitStatus" />
      <Output TaskParameter="ExitCode" PropertyName="_GitStatusExitCode" />
    </Exec>

    <PropertyGroup Condition="'$(_GitExitCode)' == '0' And '$(_GitCommitHash)' != ''">
      <!-- Trim guards against stray newlines some git/MSBuild combinations emit; an untrimmed
           value would embed whitespace in InformationalVersion. -->
      <_GitDirtySuffix Condition="'$(_GitStatusExitCode)' == '0' And '$(_GitStatus)' != ''">-dirty</_GitDirtySuffix>
      <SourceRevisionId>$(_GitCommitHash.Trim())$(_GitDirtySuffix)</SourceRevisionId>
    </PropertyGroup>

  </Target>

</Project>
```

**Notes for the implementer:**

- `IgnoreExitCode="true"` plus the guarded `PropertyGroup` means a missing `git`, or a tarball
  checkout, degrades to an unstamped assembly rather than a **build failure**. That matters: the
  house build gate is `dotnet build RotaryPhoneController.sln -warnaserror`, and an `Exec` that
  warned would fail every project.
- A root `Directory.Build.props` applies to **all nine** `.csproj` — including the four test
  projects and `src/BluetoothPoC` (`net9.0-windows`, not in the solution). Stamping those is
  harmless.

**Verify:**

```bash
cd /mnt/d/prj/rotaryphone
dotnet build src/RotaryPhoneController.Core/RotaryPhoneController.Core.csproj -c Release -f net10.0 -v quiet
# then confirm the informational version carries HEAD:
git rev-parse HEAD
strings -el src/RotaryPhoneController.Core/bin/Release/net10.0/RotaryPhoneController.Core.dll | grep -E '^1\.0\.0\+'
```

⚠ **`strings` needs `-el`.** .NET stores these literals as UTF-16 in the `#US` heap; plain ASCII
`strings` shows nothing and reads as a false negative. This exact mistake was made earlier in the
session that motivated this plan.

---

### Task 2 — `AssemblyBuildInfo` in Core

**Create** `/mnt/d/prj/rotaryphone/src/RotaryPhoneController.Core/Utilities/AssemblyBuildInfo.cs`.
Mirrors `RTest/src/Radio.Core/Utilities/AssemblyBuildInfo.cs`, with the revision parse split out as
a pure function so it is directly testable without fabricating an assembly.

```csharp
using System.Reflection;

namespace RotaryPhoneController.Core.Utilities;

/// <summary>
/// Build identity for a single assembly, read from the AssemblyInformationalVersion that the
/// .NET SDK stamps as "&lt;version&gt;+&lt;SourceRevisionId&gt;".
///
/// Throws ArgumentNullException for a null assembly and nothing else. An assembly published
/// outside a git work tree reports a GitSha of "unknown" rather than failing, because this runs
/// inside a version endpoint where throwing would turn "I cannot tell you my version" into "I am
/// down". Callers treat "unknown" as cannot-verify - which is a deploy failure, not a match.
/// </summary>
public sealed record AssemblyBuildInfo
{
    /// <summary>Reported when the assembly carries no "+revision" suffix at all.</summary>
    public const string UnknownSha = "unknown";

    private const string DirtySuffix = "-dirty";

    public required string AssemblyName { get; init; }
    public required string GitSha { get; init; }
    public required string GitShaShort { get; init; }
    public required bool IsDirty { get; init; }
    public required string InformationalVersion { get; init; }
    public required string AssemblyVersion { get; init; }
    public required DateTime BuildTimestampUtc { get; init; }

    /// <summary>
    /// Splits an AssemblyInformationalVersion into its SHA and dirty flag. Pure, so the parsing
    /// rules can be tested without building an assembly per case.
    /// </summary>
    public static (string Sha, bool IsDirty) ParseRevisionId(string informationalVersion)
    {
        string informational = informationalVersion ?? string.Empty;

        // The SDK formats this as "<version>+<SourceRevisionId>" when SourceRevisionId is set.
        // Everything after the FIRST '+' is our revision id.
        int plus = informational.IndexOf('+');
        if (plus < 0 || plus >= informational.Length - 1)
        {
            return (UnknownSha, false);
        }

        string revisionId = informational[(plus + 1)..];

        // Directory.Build.props appends "-dirty" when the tree had uncommitted changes. Split it
        // back out so GitSha stays a bare SHA that compares equal to `git rev-parse HEAD`.
        bool isDirty = revisionId.EndsWith(DirtySuffix, StringComparison.Ordinal);
        return (isDirty ? revisionId[..^DirtySuffix.Length] : revisionId, isDirty);
    }

    public static AssemblyBuildInfo For(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        AssemblyName name = assembly.GetName();

        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? string.Empty;

        (string sha, bool isDirty) = ParseRevisionId(informational);

        // Assembly.Location is the empty string for PublishSingleFile builds. RotaryPhone does not
        // publish single-file today - Deploy-ToLinux.ps1:54-63 passes --self-contained WITHOUT
        // -p:PublishSingleFile, and the box confirms it (loose DLLs beside a 78 KB apphost). The
        // fallback costs one line and stops this silently returning DateTime.MinValue if that
        // ever changes.
        string location = assembly.Location;
        if (string.IsNullOrEmpty(location))
        {
            location = Environment.ProcessPath ?? string.Empty;
        }

        DateTime buildTimestamp;
        try
        {
            buildTimestamp = string.IsNullOrEmpty(location)
                ? DateTime.MinValue
                : File.GetLastWriteTimeUtc(location);
        }
        catch
        {
            buildTimestamp = DateTime.MinValue;
        }

        return new AssemblyBuildInfo
        {
            AssemblyName = name.Name ?? string.Empty,
            GitSha = sha,
            GitShaShort = sha.Length >= 7 ? sha[..7] : sha,
            IsDirty = isDirty,
            InformationalVersion = informational,
            AssemblyVersion = name.Version?.ToString() ?? string.Empty,
            BuildTimestampUtc = buildTimestamp,
        };
    }
}
```

---

### Task 3 — `VersionReport` in Core: the whole running deployment

**Create** `/mnt/d/prj/rotaryphone/src/RotaryPhoneController.Core/Utilities/VersionReport.cs`.

```csharp
using System.Diagnostics;
using System.Reflection;

namespace RotaryPhoneController.Core.Utilities;

/// <summary>
/// Build identity for the whole running deployment, not just one assembly.
///
/// Why this has no Radio Console equivalent: they run two processes (radio-api, radio-web), each
/// a separate publish, so one SHA per endpoint is a complete answer and they verify the two
/// independently. RotaryPhone ships FOUR assemblies - Server, Core, GVBridge, GVTrunk - into ONE
/// process, so a single SHA cannot express "GVBridge is current but GVTrunk is stale". Partial
/// staleness is the exact shape of the 2026-07-29 incident, so the per-assembly list and the
/// Consistent flag are the half of the check that actually answers our failure mode.
///
/// Reports LOADED assemblies, deliberately, not the files on disk. "Files copied, service not
/// restarted" IS the incident; on-disk metadata would report the new SHA and hide it.
/// </summary>
public sealed record VersionReport
{
    public const string AssemblyPrefix = "RotaryPhoneController.";

    public required AssemblyBuildInfo Primary { get; init; }
    public required IReadOnlyList<AssemblyBuildInfo> Assemblies { get; init; }
    public required bool Consistent { get; init; }
    public required DateTime ProcessStartedAtUtc { get; init; }

    public static VersionReport Build(Assembly primary)
    {
        ArgumentNullException.ThrowIfNull(primary);

        ForceLoadOwnAssemblies();

        List<AssemblyBuildInfo> all = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .Zip(AppDomain.CurrentDomain.GetAssemblies(), (n, a) => (Name: n, Assembly: a))
            .Where(x => x.Name.StartsWith(AssemblyPrefix, StringComparison.Ordinal))
            .Where(x => !x.Name.EndsWith(".Tests", StringComparison.Ordinal))
            .Select(x => AssemblyBuildInfo.For(x.Assembly))
            .OrderBy(i => i.AssemblyName, StringComparer.Ordinal)
            .ToList();

        AssemblyBuildInfo primaryInfo = AssemblyBuildInfo.For(primary);
        if (all.Count == 0)
        {
            all = [primaryInfo];
        }

        // Compare the FULL InformationalVersion, not just the SHA: that catches a mixed
        // clean/dirty set as well as a mixed-commit set. "unknown" anywhere means we cannot
        // verify, which must never read as consistent.
        bool consistent =
            all.Select(i => i.InformationalVersion).Distinct(StringComparer.Ordinal).Count() == 1
            && all.All(i => i.GitSha != AssemblyBuildInfo.UnknownSha);

        return new VersionReport
        {
            Primary = primaryInfo,
            Assemblies = all,
            Consistent = consistent,
            ProcessStartedAtUtc = ReadProcessStartUtc(),
        };
    }

    /// <summary>
    /// Resolve every RotaryPhoneController.* assembly sitting beside us, so the report does not
    /// depend on whether lazy loading happened to have touched GVTrunk yet.
    ///
    /// Assembly.Load resolves by IDENTITY: when an assembly of that identity is already loaded it
    /// returns the LOADED instance and does not re-read a newer file on disk. That is exactly the
    /// behaviour this endpoint needs - see the class summary.
    /// </summary>
    private static void ForceLoadOwnAssemblies()
    {
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(AppContext.BaseDirectory, AssemblyPrefix + "*.dll");
        }
        catch
        {
            return;
        }

        foreach (string path in paths)
        {
            try
            {
                Assembly.Load(AssemblyName.GetAssemblyName(path));
            }
            catch
            {
                // A file we cannot load is reported by its absence from the list rather than by
                // throwing. A version endpoint must not 500.
            }
        }
    }

    private static DateTime ReadProcessStartUtc()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
```

⚠ **Implementer note.** The `Zip` over two `GetAssemblies()` calls above is fragile if the loaded
set changes between them. Replace with a single materialised call:

```csharp
        Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
        List<AssemblyBuildInfo> all = loaded
            .Where(a => (a.GetName().Name ?? string.Empty).StartsWith(AssemblyPrefix, StringComparison.Ordinal))
            .Where(a => !(a.GetName().Name ?? string.Empty).EndsWith(".Tests", StringComparison.Ordinal))
            .Select(AssemblyBuildInfo.For)
            .OrderBy(i => i.AssemblyName, StringComparer.Ordinal)
            .ToList();
```

Use this second form. The first is shown only so the review diff is unambiguous about what it
replaces.

---

### Task 4 — DTO and `HealthController` in Server

**Create** `/mnt/d/prj/rotaryphone/src/RotaryPhoneController.Server/Models/VersionInfoDto.cs`.
Follows the house DTO idiom from `GvBridgeDtos.cs:23-46`: a `record` with explicit
`[JsonPropertyName]` per property for contract stability.

```csharp
using System.Text.Json.Serialization;

namespace RotaryPhoneController.Server.Models;

/// <summary>One assembly's build identity, as it appears inside the "assemblies" array.</summary>
public record AssemblyVersionDto(
    [property: JsonPropertyName("assemblyName")] string AssemblyName,
    [property: JsonPropertyName("gitSha")] string GitSha,
    [property: JsonPropertyName("gitShaShort")] string GitShaShort,
    [property: JsonPropertyName("informationalVersion")] string InformationalVersion,
    [property: JsonPropertyName("assemblyVersion")] string AssemblyVersion,
    [property: JsonPropertyName("buildTimestampUtc")] DateTime BuildTimestampUtc,
    [property: JsonPropertyName("isDirty")] bool IsDirty);

/// <summary>
/// The /api/health/version payload.
///
/// The FIRST SIX fields are Radio Console's response contract verbatim, in their order (verified
/// live 2026-09-08 against radio:5000 and radio:5002) - after this ships there are three version
/// endpoints on one box, and one shape across all three is the entire point of mirroring.
///
/// The remaining four are RotaryPhone-specific because we ship four assemblies into one process
/// and they can go stale independently; see VersionReport.
/// </summary>
public record VersionInfoDto(
    [property: JsonPropertyName("gitSha")] string GitSha,
    [property: JsonPropertyName("gitShaShort")] string GitShaShort,
    [property: JsonPropertyName("informationalVersion")] string InformationalVersion,
    [property: JsonPropertyName("assemblyVersion")] string AssemblyVersion,
    [property: JsonPropertyName("buildTimestampUtc")] DateTime BuildTimestampUtc,
    [property: JsonPropertyName("assemblyName")] string AssemblyName,
    [property: JsonPropertyName("isDirty")] bool IsDirty,
    [property: JsonPropertyName("consistent")] bool Consistent,
    [property: JsonPropertyName("processStartedAtUtc")] DateTime ProcessStartedAtUtc,
    [property: JsonPropertyName("assemblies")] IReadOnlyList<AssemblyVersionDto> Assemblies);
```

**Create** `/mnt/d/prj/rotaryphone/src/RotaryPhoneController.Server/Controllers/HealthController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Core.Utilities;
using RotaryPhoneController.Server.Models;

namespace RotaryPhoneController.Server.Controllers;

/// <summary>
/// Build identity for deploy verification. This is the source of truth for "which commit is
/// actually running": Deploy-ToLinux.ps1 compares gitSha against the HEAD it published from and
/// exits non-zero on a mismatch.
///
/// Deliberately OUTSIDE /api/gvbridge/*, so GvBridgeAuthMiddleware does not gate it. That is a
/// decision, not an oversight - the endpoint's job is to be readable by a deploy script and by
/// Radio Console WITHOUT credentials, and an authenticated version endpoint fails closed in
/// exactly the auth-misconfiguration case you most need it to diagnose. The box is LAN-only and
/// the payload is a git SHA of a private repo. GvBridgeAuthMiddlewareTests carries a regression
/// case pinning this route as ungated; if the middleware prefix is ever broadened, that test is
/// what catches it.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    // Reflection plus one file stat per assembly, done once per process rather than per request.
    private static readonly VersionInfoDto _cached = BuildPayload();

    [HttpGet("version")]
    [ProducesResponseType(typeof(VersionInfoDto), StatusCodes.Status200OK)]
    public ActionResult<VersionInfoDto> GetVersion() => Ok(_cached);

    internal static VersionInfoDto BuildPayload()
    {
        VersionReport report = VersionReport.Build(typeof(HealthController).Assembly);

        return new VersionInfoDto(
            GitSha: report.Primary.GitSha,
            GitShaShort: report.Primary.GitShaShort,
            InformationalVersion: report.Primary.InformationalVersion,
            AssemblyVersion: report.Primary.AssemblyVersion,
            BuildTimestampUtc: report.Primary.BuildTimestampUtc,
            AssemblyName: report.Primary.AssemblyName,
            IsDirty: report.Assemblies.Any(a => a.IsDirty),
            Consistent: report.Consistent,
            ProcessStartedAtUtc: report.ProcessStartedAtUtc,
            Assemblies: [.. report.Assemblies.Select(a => new AssemblyVersionDto(
                AssemblyName: a.AssemblyName,
                GitSha: a.GitSha,
                GitShaShort: a.GitShaShort,
                InformationalVersion: a.InformationalVersion,
                AssemblyVersion: a.AssemblyVersion,
                BuildTimestampUtc: a.BuildTimestampUtc,
                IsDirty: a.IsDirty))]);
    }
}
```

**No `Program.cs` change is required.** `builder.Services.AddControllers()` (`Program.cs:84`) plus
`app.MapControllers()` (`Program.cs:410`) discover this automatically.

---

### Task 5 — Tests

All new tests go in `src/RotaryPhoneController.Server.Tests`. It already references Server pinned
to `net10.0` and carries `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, and Core
flows through transitively — so no `.csproj` change is needed.

⚠ **There is no `WebApplicationFactory` / `TestServer` harness in this repo**
(`Microsoft.AspNetCore.Mvc.Testing` is referenced by no project). Radio Console tests their
endpoint through one; we cannot without adding the package. **Do not add it for this feature** —
the house pattern is direct instantiation, and the real HTTP surface is covered by the Test Plan in
§5, which is stronger evidence anyway because it runs against the deployed binary.

**Create** `src/RotaryPhoneController.Server.Tests/Utilities/AssemblyBuildInfoTests.cs`:

```csharp
using RotaryPhoneController.Core.Utilities;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Utilities;

public class AssemblyBuildInfoTests
{
    [Theory]
    [InlineData("1.0.0+738141f81f962ed6a0d0794f16b34f36685d2e17", "738141f81f962ed6a0d0794f16b34f36685d2e17", false)]
    [InlineData("1.0.0+738141f81f962ed6a0d0794f16b34f36685d2e17-dirty", "738141f81f962ed6a0d0794f16b34f36685d2e17", true)]
    [InlineData("2.3.4-beta+abc1234", "abc1234", false)]
    public void ParseRevisionId_SplitsShaAndDirtyFlag(string informational, string expectedSha, bool expectedDirty)
    {
        (string sha, bool isDirty) = AssemblyBuildInfo.ParseRevisionId(informational);
        Assert.Equal(expectedSha, sha);
        Assert.Equal(expectedDirty, isDirty);
    }

    [Theory]
    [InlineData("1.0.0")]        // built outside a git work tree - measured, this is what you get
    [InlineData("1.0.0+")]       // trailing '+' with nothing after it
    [InlineData("")]
    [InlineData(null)]
    public void ParseRevisionId_ReportsUnknown_WhenThereIsNoRevision(string? informational)
    {
        (string sha, bool isDirty) = AssemblyBuildInfo.ParseRevisionId(informational!);
        Assert.Equal(AssemblyBuildInfo.UnknownSha, sha);
        Assert.False(isDirty);
    }

    [Fact]
    public void For_Throws_OnNullAssembly()
        => Assert.Throws<ArgumentNullException>(() => AssemblyBuildInfo.For(null!));

    [Fact]
    public void For_ReportsTheAssemblyItWasAskedAbout_NotTheCallers()
    {
        AssemblyBuildInfo info = AssemblyBuildInfo.For(typeof(AssemblyBuildInfo).Assembly);
        Assert.Equal("RotaryPhoneController.Core", info.AssemblyName);
    }

    [Fact]
    public void For_ShortShaIsAPrefixOfTheFullSha()
    {
        AssemblyBuildInfo info = AssemblyBuildInfo.For(typeof(AssemblyBuildInfo).Assembly);
        Assert.StartsWith(info.GitShaShort, info.GitSha, StringComparison.Ordinal);
    }
}
```

**Create** `src/RotaryPhoneController.Server.Tests/Utilities/VersionReportTests.cs`:

```csharp
using RotaryPhoneController.Core.Utilities;
using RotaryPhoneController.Server.Controllers;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Utilities;

public class VersionReportTests
{
    [Fact]
    public void Build_FindsEveryRotaryPhoneControllerAssembly()
    {
        VersionReport report = VersionReport.Build(typeof(HealthController).Assembly);

        // Server, Core, GVBridge, GVTrunk. If a fifth project is ever added and ships to the box,
        // this assertion is what notices that the version report now under-reports the deployment.
        Assert.All(
            new[] { "RotaryPhoneController.Server", "RotaryPhoneController.Core",
                    "RotaryPhoneController.GVBridge", "RotaryPhoneController.GVTrunk" },
            expected => Assert.Contains(report.Assemblies, a => a.AssemblyName == expected));
    }

    [Fact]
    public void Build_ExcludesTestAssemblies()
    {
        VersionReport report = VersionReport.Build(typeof(HealthController).Assembly);
        Assert.DoesNotContain(report.Assemblies, a => a.AssemblyName.EndsWith(".Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ReportsConsistent_WhenEveryAssemblyCameFromOneBuild()
    {
        // A local test run builds all four together, so they share one InformationalVersion.
        VersionReport report = VersionReport.Build(typeof(HealthController).Assembly);
        Assert.True(report.Consistent,
            "expected one build, got: " +
            string.Join(", ", report.Assemblies.Select(a => $"{a.AssemblyName}={a.InformationalVersion}")));
    }

    [Fact]
    public void Build_Throws_OnNullAssembly()
        => Assert.Throws<ArgumentNullException>(() => VersionReport.Build(null!));
}
```

**Create** `src/RotaryPhoneController.Server.Tests/Controllers/HealthControllerTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using RotaryPhoneController.Server.Controllers;
using RotaryPhoneController.Server.Models;
using Xunit;

namespace RotaryPhoneController.Server.Tests.Controllers;

public class HealthControllerTests
{
    [Fact]
    public void GetVersion_ReturnsOkWithTheHostAssemblysIdentity()
    {
        ActionResult<VersionInfoDto> result = new HealthController().GetVersion();

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        VersionInfoDto dto = Assert.IsType<VersionInfoDto>(ok.Value);

        Assert.Equal("RotaryPhoneController.Server", dto.AssemblyName);
        Assert.NotEmpty(dto.InformationalVersion);
        Assert.StartsWith(dto.GitShaShort, dto.GitSha, StringComparison.Ordinal);
        Assert.NotEmpty(dto.Assemblies);
    }

    [Fact]
    public void GetVersion_TopLevelShaMatchesTheServerEntryInTheAssembliesList()
    {
        ActionResult<VersionInfoDto> result = new HealthController().GetVersion();
        VersionInfoDto dto = (VersionInfoDto)((OkObjectResult)result.Result!).Value!;

        AssemblyVersionDto server = Assert.Single(
            dto.Assemblies, a => a.AssemblyName == "RotaryPhoneController.Server");
        Assert.Equal(dto.GitSha, server.GitSha);
    }
}
```

**Edit** `src/RotaryPhoneController.Server.Tests/Middleware/GvBridgeAuthMiddlewareTests.cs` — append
inside the existing class:

```csharp
    // Regression pin for the /api/health/version auth decision. The version endpoint MUST stay
    // reachable with no credentials even when the inter-service gate is on: Deploy-ToLinux.ps1
    // reads it to verify a deploy, and Radio Console reads it cross-service. If someone later
    // broadens the middleware's prefix, this is what fails instead of the next deploy.
    [Theory]
    [InlineData("/api/health/version")]
    [InlineData("/api/health/VERSION")]
    public async Task VersionEndpoint_IsNeverGated_EvenWithTheKeySet(string path)
        => Assert.Equal(200, await Invoke(path, header: null, configuredKey: "a-real-key"));
```

---

### Task 6 — `Deploy-ToLinux.ps1`: bake the SHA, refuse a dirty tree, verify after restart

Four edits to `/mnt/d/prj/rotaryphone/deploy/Deploy-ToLinux.ps1`.

#### 6a — parameters

**Replace** (`:30-38`):

```powershell
param(
  [switch]$NoRestart,
  [switch]$Logs,
  [string]$TargetHost = "radio",
  [string]$TargetUser = "mmack",
  [string]$TargetPath = "/opt/rotary-phone",
  [ValidateSet("linux-arm64", "linux-x64")]
  [string]$Runtime = "linux-x64"
)
```

**With:**

```powershell
param(
  [switch]$NoRestart,
  [switch]$Logs,
  # Verify only: skip build/ship/restart and just ask the box which commit it is running.
  # Doubles as the ops answer to "is radio current?" and as the way the negative test in the
  # plan's Test Plan can exercise the failure path without binary surgery.
  [switch]$VerifyOnly,
  # Stamp "<sha>-dirty" and deploy anyway. Off by default: a dirty build names a commit that is
  # not what you built.
  [switch]$AllowDirty,
  # Deploy even though git HEAD is unreadable, accepting that verification cannot run.
  [switch]$AllowUnverifiable,
  [string]$TargetHost = "radio",
  [string]$TargetUser = "mmack",
  [string]$TargetPath = "/opt/rotary-phone",
  [ValidateSet("linux-arm64", "linux-x64")]
  [string]$Runtime = "linux-x64"
)
```

#### 6b — capture the revision id

**Insert** immediately after `$SshTarget = "${TargetUser}@${TargetHost}"` (`:44`), before the
`=== Rotary Phone Deploy ===` banner:

```powershell
# --- Build identity ---------------------------------------------------------------------------
# Capture the revision id we are about to bake into every assembly, so step 5 can prove the
# running process reports the same one. Mirrors Radio Console's guarded pattern
# (RTest/deploy/Deploy-ToLinux.ps1:69-88) with two deliberate differences, both in the direction
# of failing loudly: they downgrade an unreadable git HEAD to "unknown" and SILENTLY skip
# verification, and they do not detect a dirty tree at all. A check that quietly turns itself off
# is the failure mode this whole feature exists to prevent.
$ExpectedRevisionId = $null
try {
  $gitOutput = & git -C $RepoRoot rev-parse HEAD 2>$null
  if ($LASTEXITCODE -eq 0 -and $gitOutput) {
    $trimmed = ([string]$gitOutput).Trim()
    if (-not [string]::IsNullOrWhiteSpace($trimmed)) { $ExpectedRevisionId = $trimmed }
  }
} catch {
  # git missing or repo unreadable - handled below.
}

if (-not $ExpectedRevisionId) {
  if (-not $AllowUnverifiable) {
    Write-Host "ERROR: could not read git HEAD in $RepoRoot." -ForegroundColor Red
    Write-Host "  Nothing would be able to verify this deploy, so it is refused."
    Write-Host "  Re-run with -AllowUnverifiable to ship an unverifiable build anyway."
    exit 1
  }
  Write-Host "WARNING: git HEAD unreadable - deploying WITHOUT verification." -ForegroundColor Yellow
} else {
  $dirtyFiles = & git -C $RepoRoot status --porcelain 2>$null
  if ($LASTEXITCODE -eq 0 -and $dirtyFiles) {
    if (-not $AllowDirty) {
      Write-Host "ERROR: the working tree has uncommitted changes." -ForegroundColor Red
      Write-Host "  The SHA stamped into the binary would name a commit that is not what you built."
      Write-Host "  Commit or stash, or re-run with -AllowDirty to stamp '$ExpectedRevisionId-dirty'."
      Write-Host ""
      $dirtyFiles | Write-Host
      exit 1
    }
    $ExpectedRevisionId = "$ExpectedRevisionId-dirty"
    Write-Host "WARNING: DIRTY tree - stamping $ExpectedRevisionId" -ForegroundColor Yellow
  }
}
```

#### 6c — bake it into the publish

**Replace** (`:54-63`):

```powershell
$publishArgs = @(
  "publish",
  "src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj",
  "--configuration", "Release",
  "--runtime", $Runtime,
  "-f", "net10.0",
  "--self-contained",
  "--output", $PublishDir,
  "-v", "quiet"
)
```

**With:**

```powershell
$publishArgs = @(
  "publish",
  "src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj",
  "--configuration", "Release",
  "--runtime", $Runtime,
  "-f", "net10.0",
  "--self-contained",
  # Authoritative. Directory.Build.props' _StampGitCommitHash only fills this in when it is
  # empty, so passing it here means the value the deploy verifies against and the value baked
  # into the binary are the same string, computed once.
  "-p:SourceRevisionId=$ExpectedRevisionId",
  "--output", $PublishDir,
  "-v", "quiet"
)
```

⚠ When `-AllowUnverifiable` produced a `$null` `$ExpectedRevisionId`, this renders as
`-p:SourceRevisionId=` (empty), which leaves the `Condition` in `Directory.Build.props` satisfied
and lets the fallback target run. That is the intended behaviour; do not "fix" it by omitting the
argument conditionally.

#### 6d — wrap the build/ship steps and add verification

**Wrap steps 1-4** so `-VerifyOnly` skips them. Immediately before `# --- Step 1: Build ---`
(`:51`) insert `if (-not $VerifyOnly) {`, and immediately after the restart block's closing brace
(`:224`) insert `}`. Keep the existing bodies unchanged.

**Then replace** (`:226-230` — the blank line, the banner, the two URL lines, and the trailing
blank; `:232` `if ($Logs) {` stays untouched):

```powershell
Write-Host ""
Write-Host "=== Deploy Complete ===" -ForegroundColor Green
Write-Host "  API: http://${TargetHost}:5004"
Write-Host "  Swagger: http://${TargetHost}:5004/swagger"
Write-Host ""
```

**With:**

```powershell
# --- Step 5: Verify the running process is the build we just made -----------------------------
# The 2026-07-29 incident was a stale rotary-phone binary that `systemctl status` reported as
# perfectly healthy. Every step above can succeed while the box serves old code, so this block is
# the part that actually prevents the incident. A stamp nobody checks would not have caught it -
# and on 2026-09-08 the box was found 23 commits behind with nothing flagging it.
Write-Host ""
Write-Host "[5/5] Verifying deployed commit..." -ForegroundColor Yellow

if ($NoRestart -and -not $VerifyOnly) {
  Write-Host "=== NOT VERIFIED (-NoRestart) ===" -ForegroundColor Yellow
  Write-Host "  Files were shipped but the service was not restarted, so it is still running the"
  Write-Host "  previous binary. No version check was performed."
} elseif (-not $ExpectedRevisionId) {
  Write-Host "=== NOT VERIFIED (-AllowUnverifiable) ===" -ForegroundColor Yellow
  Write-Host "  git HEAD was unreadable, so there is nothing to compare against."
} else {
  $verifyUrl = "http://${TargetHost}:5004/api/health/version"
  Write-Host "  GET $verifyUrl" -ForegroundColor DarkGray

  $deployed = $null
  for ($attempt = 1; $attempt -le 10; $attempt++) {
    try {
      $resp = Invoke-RestMethod -Uri $verifyUrl -TimeoutSec 3 -ErrorAction Stop
      if ($resp -and $resp.informationalVersion) { $deployed = $resp; break }
    } catch {
      # Service not ready yet; retry.
    }
    Start-Sleep -Seconds 2
  }

  if (-not $deployed) {
    Write-Host ""
    Write-Host "=== DEPLOY VERIFICATION FAILED ===" -ForegroundColor Red
    Write-Host "  Could not reach $verifyUrl after 10 attempts (20s)."
    Write-Host "  Check: ssh $SshTarget 'journalctl -u rotary-phone.service -n 50 --no-pager'"
    exit 1
  }

  $deployedRevision = if ($deployed.isDirty) { "$($deployed.gitSha)-dirty" } else { $deployed.gitSha }

  if ($deployedRevision -ne $ExpectedRevisionId) {
    Write-Host ""
    Write-Host "=== DEPLOY VERIFICATION FAILED ===" -ForegroundColor Red
    Write-Host "  Expected commit: $ExpectedRevisionId"
    Write-Host "  Running commit:  $deployedRevision"
    Write-Host "  Process started: $($deployed.processStartedAtUtc)"
    Write-Host "  The running process is not the build that was just published."
    Write-Host "  This is the exact failure that 'systemctl status' cannot see."
    exit 1
  }

  # Per-assembly check. A single SHA cannot express "GVBridge is current but GVTrunk is stale",
  # and that partial shape IS the 2026-07-29 incident. Radio Console's design has no equivalent
  # because they run one assembly per process; we run four in one.
  if (-not $deployed.consistent) {
    Write-Host ""
    Write-Host "=== DEPLOY VERIFICATION FAILED ===" -ForegroundColor Red
    Write-Host "  The running process is serving assemblies from more than one build:"
    foreach ($a in $deployed.assemblies) {
      Write-Host ("    {0,-42} {1}" -f $a.assemblyName, $a.informationalVersion)
    }
    Write-Host "  A partial deploy landed. Re-run the deploy; if it persists, clear $TargetPath first."
    exit 1
  }

  Write-Host ("  Verified: {0} assemblies, all at commit {1}" -f $deployed.assemblies.Count, $deployed.gitShaShort) -ForegroundColor Green
}

Write-Host ""
if ($VerifyOnly) {
  Write-Host "=== Verify Complete ===" -ForegroundColor Green
} else {
  Write-Host "=== Deploy Complete ===" -ForegroundColor Green
}
Write-Host "  API: http://${TargetHost}:5004"
Write-Host "  Swagger: http://${TargetHost}:5004/swagger"
Write-Host "  Version: http://${TargetHost}:5004/api/health/version"
Write-Host ""
```

⚠ **`$LASTEXITCODE` does not fire on native-executable failure under
`$ErrorActionPreference = "Stop"`** — the script's own comment at `:172-176` names this as *"the
silent-stale-deploy bug"*. Every new `git`/`ssh` call above checks `$LASTEXITCODE` explicitly for
that reason. Keep it that way.

---

### Task 7 — Put the short SHA where Radio Console is already looking

Radio Console polls `/api/gvbridge/status` on a 60-second cadence and today's cross-repo reply
already points them at it. Adding the short SHA there means they learn our version with **no extra
call and no new integration** — and it makes the *"which tree is authoritative"* question
self-answering, which is the mistake §0 describes.

`GVBridge` already references `Core`, so `AssemblyBuildInfo` is available.

**Edit** `src/RotaryPhoneController.GVBridge/Api/GvBridgeDtos.cs` — append one parameter to
`GvBridgeStatusDto` (`:23-46`), after `LastApiAuthFailureAt`:

```csharp
  [property: JsonPropertyName("lastApiAuthFailureAt")] DateTime? LastApiAuthFailureAt = null,
  // Short git SHA of the running GVBridge assembly. Additive with a default, per this DTO's
  // existing back-compat convention - System.Text.Json ignores unmatched members, so Radio
  // Console's deserializer is unaffected until they choose to read it. The authoritative,
  // complete answer is /api/health/version; this is the convenience copy on a payload they
  // already poll.
  [property: JsonPropertyName("gitShaShort")] string? GitShaShort = null);
```

**Edit** `src/RotaryPhoneController.GVBridge/Api/GVBridgeController.cs` — add a cached field and
pass it in `GetStatus` (`:38-58`):

```csharp
    // Computed once per process; the status endpoint is polled every 60s by Radio Console.
    private static readonly string _gitShaShort =
        RotaryPhoneController.Core.Utilities.AssemblyBuildInfo
            .For(typeof(GVBridgeController).Assembly).GitShaShort;
```

and append to the `GvBridgeStatusDto` construction:

```csharp
            LastApiAuthFailureAt: _adapter.LastApiAuthFailureAt,
            GitShaShort: _gitShaShort));
```

---

### Task 8 — Docs and the cross-repo reply

1. **`docs/handoffs/radioconsole-build-stamp-reply.md`** (new) — the reply owed since 2026-07-31
   under the boundary doc's § *"Passing Work Between Sessions"*. It must:
   - Answer §2.3 (*"which tree is authoritative"*) with the binary evidence: **`D:\prj\RotaryPhone`
     is authoritative**; `rp-deploy` is an orphaned worktree of the same repo frozen at PR #68 and
     is not what runs. **Explicitly retract** the `✅ SETTLED 2026-07-31` claim in
     `CROSS-REPO-HANDOFFS.md:11` and note that `GV-5` can unblock — ADR-028 was derived from the
     correct tree all along.
   - Give them `http://192.168.86.50:5004/api/health/version`, note that the first six fields match
     theirs exactly, and flag `assemblies[]`/`consistent` as our addition and why.
   - Note `gitShaShort` is now also on `/api/gvbridge/status`.
   - State plainly that §1 (CDP log spam) is **not** addressed here and is tracked separately.
2. **`README.md`** — a Deployment subsection: how to check what is running
   (`curl radio:5004/api/health/version`), what `consistent:false` means, and
   `Deploy-ToLinux.ps1 -VerifyOnly` as the one-command answer.
3. **`CLAUDE.md`** — a short § *Deployment* note: **never** answer "is X deployed?" by
   `strings`-grepping a DLL. Use the endpoint. If a binary must be inspected directly, `strings`
   **requires `-el`** — .NET stores these literals as UTF-16 and plain ASCII `strings` returns a
   false negative. This session made that exact error.
4. **`docs/KNOWN-ISSUES.md`** — record that the box ran 23 commits behind for five weeks with
   nothing flagging it, and that this feature closes it.

---

### Task 9 — Quality gates and PR

```bash
cd /mnt/d/prj/rotaryphone
dotnet build RotaryPhoneController.sln -warnaserror
dotnet test  RotaryPhoneController.sln
```

Then run the full §5 Test Plan against the box — **including the negative tests**, which are the
point. Open the PR with a **Docs Impact** section listing the four doc changes in Task 8.

**Merge judgement.** This touches the deploy script, which is how everything else reaches the box —
a bug here breaks all future deploys. Per the auto-merge policy: merge on green gates **only if**
Test Plan cases `N1`, `N2` and `N3` all demonstrably failed the deploy. **Pause and ask** if any
negative test did not fail, or if the box is left in a state needing manual repair.

---

## 5. Test Plan

For a Tester, against the running service on `radio` (`192.168.86.50:5004`).

⚠ **Bound every journald read with `--since` and never tail.** Heavy journald reads on the N100
compete with the audio pipeline and correlate with audio distortion.

### Positive cases

**P1 — the endpoint answers, and answers correctly.**
```bash
curl -s http://radio:5004/api/health/version | jq
cd /mnt/d/prj/rotaryphone && git rev-parse HEAD
```
**PASS:** HTTP 200; `gitSha` equals `git rev-parse HEAD` exactly; `gitShaShort` is its first 7
chars; `assemblyName` is `RotaryPhoneController.Server`; `isDirty` is `false`; `consistent` is
`true`; `assemblies` has **4** entries (Core, GVBridge, GVTrunk, Server) all sharing one
`informationalVersion`.

**P2 — it is JSON, not the SPA fallback.** `Program.cs:418` `MapFallbackToFile("index.html")`
returns HTML with **HTTP 200** for any unmatched path, so a route that failed to register looks
like a success.
```bash
curl -s -D- -o /dev/null http://radio:5004/api/health/version | grep -i content-type
```
**PASS:** `Content-Type: application/json`. **FAIL** if `text/html` — the route did not register.

> ⚠ **THIS CHECK IS NOW WEAKER THAN IT READS — 2026-09-09. Assert the status code too.** As written
> it can no longer detect the failure it was built to detect.
>
> `Program.cs` now returns a **JSON** 404 for unmatched `/api/*` paths instead of the SPA shell. So
> a `/api/health/version` that **failed to register** now answers `404 application/json` — which
> satisfies the PASS condition above (`Content-Type: application/json`) and never trips the stated
> FAIL condition (`text/html`). The content-type discriminator that made this check work has been
> removed by the fix it was written before.
>
> **Use this instead** — it checks the status line as well, and does not depend on the fallback's
> content type:
>
> ```bash
> curl -s -o /dev/null -w '%{http_code} %{content_type}\n' http://radio:5004/api/health/version
> ```
>
> **PASS:** `200 application/json`. **FAIL** on any other status — `404 application/json` now means
> exactly what the original check meant by `text/html`: **the route did not register.**
>
> The prose above it ("returns HTML with HTTP 200 for any unmatched path") is still true for
> non-`/api/*` paths, and false for `/api/*` since this change.

**P3 — no credentials needed.**
```bash
curl -s -o /dev/null -w '%{http_code}\n' http://192.168.86.50:5004/api/health/version
```
**PASS:** `200`, from a host that is not the box, with no `X-RotaryPhone-Auth` header.

**P4 — the short SHA reached the status payload.**
```bash
curl -s http://radio:5004/api/gvbridge/status | jq '.gitShaShort'
```
**PASS:** matches `gitShaShort` from P1. Also confirm the pre-existing fields (`available`,
`cookiesValid`, `authBlackout`, …) are all still present and unchanged.

**P5 — shape parity with Radio Console.**
```bash
ssh mmack@radio 'curl -s localhost:5000/api/health/version' | jq 'keys'
curl -s http://radio:5004/api/health/version | jq 'keys'
```
**PASS:** ours is a strict superset; the six shared keys have identical names.

**P6 — `-VerifyOnly` passes on a current box.**
```powershell
.\deploy\Deploy-ToLinux.ps1 -VerifyOnly
```
**PASS:** prints `Verified: 4 assemblies, all at commit <short>`, `=== Verify Complete ===`,
`$LASTEXITCODE` is `0`, and it neither built nor restarted anything.

### Negative cases — a check that has never been seen to fail is not known to work

**N1 — whole-deployment mismatch.** No binary surgery; move HEAD and ask the box.
```powershell
git commit --allow-empty -m "negative test: HEAD moves, box does not"
.\deploy\Deploy-ToLinux.ps1 -VerifyOnly
echo $LASTEXITCODE
git reset --hard HEAD~1
```
**PASS:** red `=== DEPLOY VERIFICATION FAILED ===`, both SHAs printed and different,
`$LASTEXITCODE` is `1`. **FAIL** if it prints `Verify Complete`.

**N2 — partial staleness. This is the 2026-07-29 incident, reproduced.**

Build a GVTrunk assembly stamped with a bogus revision and drop only that one file onto the box.
**Do not use a historical DLL** — an August build may not bind against a current Server and could
leave the service down. A same-source rebuild with a different stamp differs *only* in the stamp,
which is exactly the property we want. (`-p:SourceRevisionId=` overriding the fallback target is
measured behaviour, not an assumption.)

```powershell
dotnet publish src/RotaryPhoneController.Server/RotaryPhoneController.Server.csproj `
  -c Release -r linux-x64 -f net10.0 --self-contained `
  -p:SourceRevisionId=deadbeefdeadbeefdeadbeefdeadbeefdeadbeef `
  -o publish\negative-test -v quiet

scp publish\negative-test\RotaryPhoneController.GVTrunk.dll mmack@radio:/tmp/
ssh mmack@radio 'sudo install -o mmack -g mmack -m 644 /tmp/RotaryPhoneController.GVTrunk.dll /opt/rotary-phone/RotaryPhoneController.GVTrunk.dll && sudo systemctl restart rotary-phone.service'
```
```bash
sleep 8 && curl -s http://radio:5004/api/health/version | jq '{consistent, assemblies: [.assemblies[] | {assemblyName, gitShaShort}]}'
```
**PASS:** `consistent` is `false`; `RotaryPhoneController.GVTrunk` shows `deadbee` while the other
three show the real short SHA. Then:
```powershell
.\deploy\Deploy-ToLinux.ps1 -VerifyOnly
echo $LASTEXITCODE
```
**PASS:** fails with *"serving assemblies from more than one build"*, lists all four with their
versions, `$LASTEXITCODE` is `1`.

⚠ **Restore before moving on:** `.\deploy\Deploy-ToLinux.ps1` (a normal full deploy), then re-run
P1 and confirm `consistent: true`. Record in the report that the box was restored.

**N3 — a dirty tree is refused.**
```powershell
"scratch" | Out-File -Encoding utf8 .\negative-test-scratch.txt
.\deploy\Deploy-ToLinux.ps1
echo $LASTEXITCODE
Remove-Item .\negative-test-scratch.txt
```
**PASS:** refuses **before building**, names the offending file, `$LASTEXITCODE` is `1`. Then
confirm `-AllowDirty` proceeds and stamps `-dirty`, and that
`curl .../api/health/version | jq '.isDirty'` is `true`. **Restore with a clean deploy afterwards.**

**N4 — an unreachable service fails, and does not hang.**
```powershell
ssh mmack@radio 'sudo systemctl stop rotary-phone.service'
Measure-Command { .\deploy\Deploy-ToLinux.ps1 -VerifyOnly }
ssh mmack@radio 'sudo systemctl start rotary-phone.service'
```
**PASS:** *"Could not reach … after 10 attempts"*, exit `1`, elapsed ≈ 20-50s (10 × 2s sleep + up
to 10 × 3s timeout), not indefinite.

**N5 — `-NoRestart` says so loudly.**
```powershell
.\deploy\Deploy-ToLinux.ps1 -NoRestart
```
**PASS:** yellow `=== NOT VERIFIED (-NoRestart) ===` naming why. This is the bypass Radio Console
has silently; here it must be visible. **Restore with a normal deploy.**

### Reporting

Record: every command's exact output; `$LASTEXITCODE` for each of N1-N5; the four-assembly list
from P1; confirmation the box was restored to a clean, `consistent: true` state at the end; and the
`git rev-parse HEAD` the box was left on.

---

## 6. Explicitly out of scope

- **The React SPA has no version story.** `wwwroot` is committed with content-hashed asset names
  and `Deploy-ToLinux.ps1` never rebuilds it — only `publish.ps1` does, and it is not part of the
  deploy. A stale-frontend deploy is a real, currently invisible failure mode that this feature
  does **not** cover. Radio Console hit the equivalent and fixed it separately as `OPS-5`
  (`Cache-Control` on static assets). **Worth its own item.**
- **§1 of the originating request (CDP log spam on port 9224).** Unrelated to the build stamp;
  tracked separately. Say so explicitly in the Task 8 reply.
- **`rotary-phone-cookies.service`.** A oneshot cookie refresh, currently `inactive dead`; it ships
  no assembly and cannot go stale in the sense this feature addresses.
- **An architecture guard (`uname -m` before publish).** Radio Console proposed it as `OPS-1(c)`
  and **did not build it** — they changed the defaults instead, leaving an explicit wrong
  `-Runtime` unguarded. Our default is already `linux-x64` and the box is `x86_64`, so the risk is
  latent. Noted, not fixed here.
- **Deleting `/mnt/d/prj/rp-deploy`.** It has misled a partner team and should probably go, but
  that is the owner's call and touches nothing in this repo.

---

## 7. What we deliberately did not mirror from `OPS-1`

| Their choice | Ours | Why |
|---|---|---|
| One SHA per endpoint, one assembly | **Per-assembly list + `consistent`** | Two processes vs four assemblies in one process. §2.2. |
| Dirty tree undetected | **`-dirty` suffix + `isDirty` + deploy refuses by default** | They have CI-shaped discipline; we deploy by hand from a dev box with no CI at all. |
| Unreadable git HEAD → warn, skip verification | **Hard fail unless `-AllowUnverifiable`** | A check that silently disables itself is the failure class this feature exists to remove. |
| `-NoRestart` silently skips verification | **Prints `=== NOT VERIFIED ===`** | Same reason. Their bypass is invisible in the output. |
| `IncludeSourceRevisionInInformationalVersion` left to the SDK default | **Set explicitly** | Our stamp is *entirely* implicit today; leaving both ends implicit is two silent dependencies stacked. |
| No `-VerifyOnly` | **Added** | Makes the check independently runnable — which is what lets N1 exercise the failure path without touching binaries, and gives ops a one-command "is the box current?". |
| `Radio.Web`'s endpoint has **no test** | **Tested** | Their own gap; agent-verified. |
| Their bash sibling (`deploy-to-pi.sh`) verifies the API only | **N/A** | We have one deploy script. |
| `assemblyName` distinguishes responses | **Kept** | Free, and it keeps the six shared fields meaningful. |

**Mirrored deliberately and exactly:** the endpoint path, the first six field names and their
order, the `_StampGitCommitHash` target name and `Exec` flags, the `Condition` that makes the
MSBuild target a fallback under an explicit `-p:SourceRevisionId`, the 10 × 2s / 3s-timeout poll,
the red `=== DEPLOY VERIFICATION FAILED ===` banner, `exit 1`, and the `Assembly.Location` →
`Environment.ProcessPath` fallback.

---

## 8. Assumptions

1. **The build host is Windows with a .NET 10 SDK.** The projects target `net10.0`; the deploy is
   PowerShell; the WSL side here has only SDK `9.0.115`, so `dotnet build` of the solution was not
   run as part of writing this plan. The `Directory.Build.props` mechanism was verified on `net9.0`
   with SDK 9.0.115, which exercises the same SDK code path — but **Task 1's verify step must be
   run on the real build host before trusting it**.
2. **The SDK's implicit SourceLink behaviour is what produces today's stamp.** Verified by
   construction (a bare classlib in a fresh git repo stamps; the same sources outside a git repo do
   not). Not verified by reading SDK targets. The plan does not depend on the *cause* — Task 1
   makes the behaviour explicit either way.
3. **`Assembly.Load` returning the already-loaded instance for a matching identity.** Standard
   default-ALC behaviour, and load-bearing for §2.1. N2 exercises it directly: if it re-read from
   disk, the bogus GVTrunk would still show `deadbee` and the test still passes — so N2 validates
   the *outcome* regardless.
4. **`AssemblyVersion` stays `1.0.0.0` for all four assemblies**, so a swapped DLL binds. True
   today (no `<Version>` anywhere in the tree). If per-project versions are ever introduced, N2's
   mechanics change.
5. **Radio Console's deserializer ignores unknown JSON members.** `System.Text.Json`'s default;
   makes the Task 7 status-payload addition non-breaking. Their side should still be told (Task 8).
6. **The plan does not know what changed in `GVTrunk` between `738141f` and `3c2c892`.** This is
   why N2 uses a same-source rebuild rather than the August DLL.

---

## 9. Self-review

- **Spec coverage.** All four asks in the request are covered: stamp (Tasks 1-2), expose (Tasks
  3-4), verify at deploy (Task 6), discoverable (Tasks 7-8). The `/version` shape, host, and auth
  decisions are each stated with a justification and a rejected alternative (§3).
- **Placeholders.** None. No `TBD`, no "similar to Task N", no "implement later". Every task
  carries literal code. The one place showing two forms (Task 3's `Zip`) says explicitly which to
  use and why the other appears.
- **Type consistency.** `AssemblyBuildInfo` → `VersionReport` → `VersionInfoDto` field names and
  types line up; `ParseRevisionId` returns the tuple both callers destructure.
- **Verified rather than assumed:** the existing stamp (box, all four DLLs); the no-git degradation
  and the `-p:` override (both built and measured); Radio Console's live response shape (curled,
  not read); production port 5004 (systemd unit); the auth middleware's prefix-gate model (read in
  full); the absence of `Directory.Build.props`, CI, and any `WebApplicationFactory`; that build
  outputs are gitignored so a deploy cannot self-dirty.
- **Known weak points.** Assumption 1 (untested on the real SDK) is the largest and is gated by
  Task 1's verify step. `buildTimestampUtc` is a hint, not evidence — stated in §3.4. The SPA
  staleness gap is real and deliberately unaddressed (§6).
