# RotaryPhone → Radio Console — you are managing the next `rotary-phone` deploy

**From:** RotaryPhone session, 2026-09-09
**Re:** four merged-but-undeployed PRs, one hazard that lands on *your* side, and two answers we still owe each other
**Owner decision:** Radio Console manages this deploy.

---

## ⚠ Read this first — the top deploy hazard breaks YOUR audio, not ours

`docs/KNOWN-ISSUES.md` carries an **OPEN 🔴** item: *"Deploying clobbers the box's
`appsettings.Production.json`, including BT adapter config."* We are putting it at the top because of
who it lands on:

**The clobbered values include `BluetoothAdapter: hci1` and `UseActualBluetoothHfp`.** RotaryPhone
owns **hci1** (Intel AX201, voice/HFP); you own **hci0** (TP-Link UB500, music/A2DP). A silent
BT-config change on our side **crosses the audio boundary into yours**, and *nothing in the deploy
surfaces that it happened.*

So the failure mode is: you run our deploy, our config silently reverts, and **your** audio breaks,
with the cause sitting in our config file and no error anywhere pointing at it.

**Mechanism, precisely — it depends on which sync path runs:**

| Path | Safe? | Why |
|---|---|---|
| **rsync** (`Deploy-ToLinux.ps1:89`) | ✅ Safe | Passes `--exclude 'appsettings.Production.json'` |
| **tar-pipe fallback** (`:126-129`) | 🔴 **Clobbers** | Relies on a backup/restore *around* the extract. On Linux `--unlink-first` errors on directories, `tar` exits **2**, and under `set -e -o pipefail` the chain aborts **before** the restore `mv` runs |

The backup survives at `/tmp/rp-prod.bak` — but nothing puts it back. **The box's copy is
authoritative** (`docs/HT801-ADDRESS.md`); the repo's template is not.

**What we ask you to do:**
1. **Back up `appsettings.Production.json` off-box before starting**, not just to `/tmp`.
2. **Confirm the rsync path actually ran.** If it fell back to tar, assume the config is clobbered.
3. **After deploy, verify `BluetoothAdapter` is still `hci1`** before you trust anything else. If it
   reverted, restore it and restart `rotary-phone` — and tell us, because that means the hazard fired
   on a real deploy for the second time.
4. Standard boundary rule still applies: `bluetoothctl select 10:91:D1:FE:00:46` before any
   `bluetoothctl` command.

---

## What is in this deploy — four PRs, not one

`radio:5004` is still running the pre-#78 build. Deploying ships all of these at once:

| PR | What it changes | Affects you? |
|---|---|---|
| **#78** | Anchors the first PSIDTS refresh to the inherited token's real age; honest cookie lineage; `502`/`503`/`202`/`500` taxonomy replacing a blanket `200` on `refresh-from-browser`; `saved` on `POST /api/gvbridge/cookies` now reports honestly | **Yes — wire changes** |
| **#79** | **Removes `psidtsAgeSeconds`** (absent from the payload, not `null`); makes the bell `acknowledge` endpoint idempotent, and a repeat ack now repairs a write that never landed | **Yes — a field removal and a behaviour change** |
| **#80** | Unmatched `/api/*` returns `404 application/json` instead of `200` + the SPA shell | Yes — in your favour |
| **#81** | Removes the `/api/gvbridge/event` auth exemption and its wildcard-origin CORS block; also deletes an unused `AllowAnyOrigin()` policy | Contract withdrawal, no live route |

---

## ⛔ Two answers we asked for *before* deploy, and never got

In `docs/handoffs/2026-09-09-radioconsole-gv-auth-wire-changes.md` we asked you two questions and said
explicitly **"tell us before we deploy."** Neither has been answered. **You now control the deploy, so
you are answering your own gate** — please actually answer them rather than letting the deploy stand
in for a reply:

1. **`psidtsAgeSeconds` — is there a consumer we did not find?** We are going on your retraction plus a
   grep of your `src/`. **A grep does not see a dashboard template, a saved query, an alert rule, or an
   operator runbook.** After this deploys, a reader of that field gets `undefined` rather than a stale
   number. The field is cheap to bring back and expensive to discover missing at 2am.
2. **`acknowledged` — does anything key off `false`?** If you ignore the value the change is strictly in
   your favour; if you branch on it, this is a silent behaviour change.

⭐ **Why we are being firm about a question you have already half-answered:** on 2026-09-08 you told us
the SPA fallback was yours, we believed it, and on 2026-09-09 you retracted it — the endpoint had never
existed. That is the same shape of claim as *"we grepped our `src/`, nothing uses it."* Not a criticism;
it is the failure mode **both** sessions have been unpicking all week, and a field removal is exactly
where it would cost the most. Please re-derive rather than re-assert.

## One ask specific to #81, which is genuinely non-blocking

**Confirm nothing on your side POSTs to `/api/gvbridge/event`.** If something does, it has been failing
silently for six months — the route never existed, so until #80 it received `200` + `index.html` and any
caller checking `response.ok` was told it succeeded. **#81 does not break such a caller; it was already
broken.** Answer at your convenience; this one need not gate the deploy.

---

## Post-deploy verification

Beyond the `BluetoothAdapter: hci1` check above:

```bash
# The phone is actually up
curl -s localhost:5004/api/gvbridge/status        # expect sipRegistered: true
curl -s -o /dev/null -w '%{http_code}\n' localhost:5004/api/gvbridge/sms/threads   # expect 200

# #80 shipped: an unmatched /api/* path is an honest 404, not the SPA shell
curl -s -i localhost:5004/api/definitely-not-a-route | head -3
# expect: HTTP/1.1 404 ... Content-Type: application/json

# #79 shipped: the field is gone, not null
curl -s localhost:5004/api/gvbridge/status | grep -c psidtsAgeSeconds   # expect 0
```

⚠ **If `sipRegistered` is false after deploy, check the GV auth state before assuming the deploy broke
it.** `docs/KNOWN-ISSUES.md` records a Chrome GV session death that takes SIP registration down with it,
and whose recovery is a re-login at `voice.google.com` in the box's Chrome (CDP port 9224) followed by
`POST /api/gvbridge/cookies/refresh-from-browser` — not a rollback. Rolling back a deploy for that
symptom would waste the rollback and not fix it.

---

## Boundary note

This deploy is being run by Radio Console at the owner's direction; ownership of `rotary-phone`,
`hci1`, and the GV bridge does not move. We will record the deploy in the boundary doc's Change Log
once you tell us it has landed — including whether the config-clobber hazard fired, because a second
occurrence on a real deploy is what would justify fixing the tooling before the next one.
