#!/usr/bin/env python3
"""GV auto-relogin sign-in driver -- OWNER-WRITTEN. Skeleton to flesh out.

Contract: docs/gv-relogin-driver-contract.md (read it in full; section numbers below refer to it).
Page facts: docs/spikes/2026-09-09-gv-signin-cdp-recording.md (contract §8 summarises them).

How to use this skeleton
------------------------
1. Copy it to deploy/gv-relogin-signin.py (the deploy ships deploy/*.py; the actuator treats
   the file's PRESENCE as "auto-relogin installed", so do not copy it until it is finished).
2. Fill in the four stubs marked OWNER: open_entry, choose_account, submit_password_once,
   classify_after_submit. Everything else is contract plumbing and should not need changing.
3. In WSL:  bash deploy/tests/check-relogin-driver.sh   (never on radio -- contract §9.2)
4. Review contract §9.3 yourself, then the attended runs (plan Task 17).

What the plumbing already guarantees
------------------------------------
* stdin is read to EOF and validated before anything else; bad input -> UNRECOGNISED (§4.2).
* The credential lives only in memory: never printed, never in an exception message, never
  repr()'d, never in argv/env/disk (§4.3, §7.6). Error reports print the exception TYPE only.
* TRANSPORT is printed ONLY for faults before the first command is sent to the target page
  (§5.1). The `touched` flag flips immediately before that first send; after it, every fault
  is UNRECOGNISED.
* Exit status is always 0 and the verdict is always the last stdout line (§5.1).
* Time budgets: 15 s per navigation, 30 s submit->settled, and a hard stop well under the
  actuator's 120 s kill (§7.5).
* websocket-client is imported lazily, after target discovery, so the checker's dead-port case
  reaches TRANSPORT even on a machine without websocket-client (WSL has none).

Rules the stubs must keep (contract §7) -- the plumbing cannot enforce these for you
------------------------------------------------------------------------------------
* Drive ONLY the page `target_id`. Never start/close a browser, never open/close targets,
  never clear cookies/storage/cache/profile. Same profile is the whole safety argument (§7.1).
* Submit the password AT MOST ONCE. No re-type, no re-submit, no "try again" loop (§7.2).
* Classify on what is RENDERED, never on what is present in the DOM -- Google pre-renders a
  hidden "Too many failed attempts" region and a dormant CAPTCHA on every password page (§7.3).
  Use is_rendered() below.
* Anything you did not positively recognise is UNRECOGNISED (§7.4).
* Do not call the RotaryPhone service and do not read the credential file (§7.7).
"""
import json
import sys
import time
import urllib.error
import urllib.request

# --- verdict words (contract §5.1) -------------------------------------------------------
SIGNED_IN = "SIGNED_IN"
CREDENTIAL_REJECTED = "CREDENTIAL_REJECTED"
CHALLENGED = "CHALLENGED"
TRANSPORT = "TRANSPORT"
UNRECOGNISED = "UNRECOGNISED"

KEYS = ("version", "cdp_port", "target_id", "email", "password")

# --- time budgets (contract §7.5, spike "Timings") ----------------------------------------
STEP_TIMEOUT_S = 15.0        # per navigation
SUBMIT_SETTLE_S = 30.0       # submit -> settled URL (the spike marks this one as an estimate)
RUN_BUDGET_S = 100.0         # our own ceiling, safely under the actuator's 120 s kill
POLL_INTERVAL_S = 0.25

_T0 = time.monotonic()


class Transport(Exception):
    """A fault positively identified as happening BEFORE the first command to the page."""


class Unrecognised(Exception):
    """Any state or fault we did not positively identify. The default."""


def log(msg):
    """Diagnostics to stderr (journal, redacted as a second line of defence -- §5.3).
    Log WHICH STATE you observed, never what you typed."""
    print(f"gv-relogin-signin: {msg}", file=sys.stderr)


def remaining():
    left = RUN_BUDGET_S - (time.monotonic() - _T0)
    if left <= 0:
        raise Unrecognised("run budget exhausted")
    return left


# --- input (contract §4.2) ---------------------------------------------------------------
def read_input():
    """Read stdin to EOF; return the five fields, or None if the input is not exactly valid.
    Value = everything after the FIRST '='. Never print or repr() the result."""
    raw = sys.stdin.read()
    fields = {}
    for line in raw.split("\n"):
        if not line:
            continue
        if "=" not in line or "\r" in line:
            return None
        key, value = line.split("=", 1)
        if key in fields or key not in KEYS:
            return None
        fields[key] = value
    if fields.get("version") != "1" or any(not fields.get(k) for k in KEYS):
        return None
    try:
        fields["cdp_port"] = int(fields["cdp_port"])
    except ValueError:
        return None
    return fields


# --- CDP plumbing ------------------------------------------------------------------------
def find_target_ws_url(port, target_id):
    """Pre-interaction discovery. Every failure here is legitimately TRANSPORT (§5.1)."""
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list",
                                    timeout=min(STEP_TIMEOUT_S, remaining())) as r:
            listing = json.loads(r.read())
    except (urllib.error.URLError, OSError, ValueError) as e:
        raise Transport(f"CDP port did not answer /json/list ({type(e).__name__})") from None
    for t in listing:
        if t.get("type") == "page" and t.get("id") == target_id:
            return t["webSocketDebuggerUrl"]
    raise Transport("target_id is not among the page targets")


class Page:
    """One websocket to the ONE page the actuator chose. `touched` flips to True immediately
    before the first command is sent; from then on no fault may be reported as TRANSPORT."""

    def __init__(self, ws_url):
        import websocket  # lazy: see module docstring
        self._ws_mod = websocket
        try:
            self.ws = websocket.create_connection(ws_url, timeout=min(STEP_TIMEOUT_S, remaining()))
        except (OSError, websocket.WebSocketException) as e:
            raise Transport(f"websocket to target failed ({type(e).__name__})") from None
        self.touched = False
        self.n = 0
        self.events = []

    def send(self, method, **params):
        self.touched = True  # ⛔ must precede the first byte to the page
        self.n += 1
        self.ws.settimeout(min(STEP_TIMEOUT_S, remaining()))
        self.ws.send(json.dumps({"id": self.n, "method": method, "params": params}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == self.n:
                if "error" in msg:
                    raise Unrecognised(f"{method} returned a protocol error")
                return msg.get("result", {})
            if "method" in msg:
                self.events.append(msg)

    def wait_for(self, event, timeout):
        """Wait for a CDP event (e.g. Page.loadEventFired), consuming buffered ones first."""
        for i, msg in enumerate(self.events):
            if msg.get("method") == event:
                del self.events[: i + 1]
                return msg.get("params", {})
        self.events.clear()
        deadline = time.monotonic() + min(timeout, remaining())
        while True:
            left = deadline - time.monotonic()
            if left <= 0:
                raise Unrecognised(f"no {event} within budget")
            self.ws.settimeout(left)
            msg = json.loads(self.ws.recv())
            if msg.get("method") == event:
                return msg.get("params", {})

    def evaluate(self, expression):
        """Evaluate a READ-ONLY expression in the page and return its value.
        ⛔ Never interpolate the credential into `expression` -- use a CDP input method
        for anything the owner decides to type (see submit_password_once)."""
        r = self.send("Runtime.evaluate", expression=expression, returnByValue=True)
        if r.get("exceptionDetails"):
            raise Unrecognised("page script raised")
        return r.get("result", {}).get("value")

    def live_href(self):
        """The page's OWN location -- never /json/list's cached url (KNOWN-ISSUES.md:16-22)."""
        return self.evaluate("window.location.href")

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def is_rendered(page, selector):
    """True only if `selector` matches an element that is actually RENDERED (contract §7.3):
    has an offsetParent (or is position:fixed), a non-zero box, and is not display:none /
    visibility:hidden. Presence in the DOM is NOT enough -- Google pre-renders hidden templates."""
    expr = (
        "(() => { const e = document.querySelector(%s); if (!e) return false;"
        " const s = getComputedStyle(e); const r = e.getBoundingClientRect();"
        " return s.display !== 'none' && s.visibility !== 'hidden' && r.width > 0 && r.height > 0"
        " && (e.offsetParent !== null || s.position === 'fixed'); })()" % json.dumps(selector)
    )
    return bool(page.evaluate(expr))


def rendered_text(page, selector):
    """innerText of `selector` if rendered, else None. For reading e.g. the #c0 message."""
    if not is_rendered(page, selector):
        return None
    return page.evaluate("document.querySelector(%s).innerText" % json.dumps(selector))


def wait_until(predicate, timeout, what):
    """Poll `predicate()` until truthy or budget runs out -> Unrecognised. Returns its value."""
    deadline = time.monotonic() + min(timeout, remaining())
    while time.monotonic() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(POLL_INTERVAL_S)
    raise Unrecognised(f"timed out waiting for {what}")


# --- OWNER: the sign-in flow ---------------------------------------------------------------
# Each stub raises NotImplementedError, which main() reports as UNRECOGNISED -- so an
# unfinished driver can only ever trip the breaker, never make a partial attempt look OK.

def open_entry(page):
    """OWNER. Bring the target page to the sign-in entry and confirm where it landed.
    Page facts: contract §8 row 1 (entry URL and its redirect to the account chooser).
    Budget: STEP_TIMEOUT_S. Return normally only when the landing state is one you recognise;
    otherwise raise Unrecognised."""
    raise NotImplementedError("open_entry")


def choose_account(page, email):
    """OWNER. Get from the account chooser to the password page for `email`.
    Page facts: contract §8 rows 2-3. If the chooser is absent, shows zero or several matching
    accounts, or the email-entry path appears (never observed), raise Unrecognised.
    Budget: STEP_TIMEOUT_S. Return only once the password page is positively recognised."""
    raise NotImplementedError("choose_account")


def submit_password_once(page, password):
    """OWNER. Enter the password and submit -- EXACTLY ONCE (contract §7.2).
    Page facts: contract §8 row 4. Keep `password` out of logs, exceptions, and any
    Runtime.evaluate expression string. After submitting, return; do not wait or retry here."""
    raise NotImplementedError("submit_password_once")


def classify_after_submit(page):
    """OWNER. Wait up to SUBMIT_SETTLE_S for a state you can POSITIVELY identify, then return one
    of SIGNED_IN, CREDENTIAL_REJECTED, CHALLENGED. Anything else -> raise Unrecognised.
    Page facts: contract §8 rows 5 (success), 6 (rejection), 8 + §7.3 (hidden templates).
    Use live_href() and is_rendered()/rendered_text(); never test presence alone.
    Leave the page where it settled (contract §6 -- the actuator verifies from there)."""
    raise NotImplementedError("classify_after_submit")


# --- main: contract plumbing ---------------------------------------------------------------
def run(fields):
    ws_url = find_target_ws_url(fields["cdp_port"], fields["target_id"])  # TRANSPORT-legal
    page = Page(ws_url)                                                     # TRANSPORT-legal
    try:
        try:
            open_entry(page)
            choose_account(page, fields["email"])
            submit_password_once(page, fields["password"])
            return classify_after_submit(page)
        except Transport:
            # A Transport raised after the first send is not legal as TRANSPORT (§5.1).
            if page.touched:
                raise Unrecognised("transport fault after first page interaction") from None
            raise
        except (OSError, page._ws_mod.WebSocketException, ValueError, KeyError) as e:
            if page.touched:
                raise Unrecognised(f"fault after first page interaction ({type(e).__name__})") from None
            raise Transport(f"fault before first page interaction ({type(e).__name__})") from None
    finally:
        page.close()


def main():
    fields = read_input()
    if fields is None:
        log("stdin not a valid version=1 request")
        print(UNRECOGNISED)
        return 0
    try:
        verdict = run(fields)
        if verdict not in (SIGNED_IN, CREDENTIAL_REJECTED, CHALLENGED):
            log("flow returned no recognised verdict")
            verdict = UNRECOGNISED
    except Transport as e:
        log(f"transport: {e}")
        verdict = TRANSPORT
    except Unrecognised as e:
        log(f"unrecognised: {e}")
        verdict = UNRECOGNISED
    except NotImplementedError as e:
        log(f"not implemented: {e}")
        verdict = UNRECOGNISED
    except BaseException as e:  # noqa: BLE001 -- never let a traceback (or a secret in one) out
        log(f"unexpected {type(e).__name__}")
        verdict = UNRECOGNISED
    finally:
        fields = None  # drop the reference to the credential as early as possible
    print(verdict)
    return 0


if __name__ == "__main__":
    sys.exit(main())
