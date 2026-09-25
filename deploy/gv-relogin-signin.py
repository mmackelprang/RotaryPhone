#!/usr/bin/env python3
"""GV auto-relogin sign-in driver -- OWNER-WRITTEN.

Contract: docs/gv-relogin-driver-contract.md (read it in full; section numbers below refer to it).
Page facts: docs/spikes/2026-09-09-gv-signin-cdp-recording.md (contract §8 summarises them).
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

# --- Page constants ----------------------------------------------------------------------
ENTRY_URL = "https://accounts.google.com/ServiceLogin?continue=https://voice.google.com/"
CHOOSER_ITEM = 'div[role="link"][data-identifier][data-button-type="multipleChoiceIdentifier"]'

AT_CHOOSER = "at_chooser"
ALREADY_SIGNED_IN = "already_signed_in"


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


def _host_and_path(href):
    from urllib.parse import urlsplit
    u = urlsplit(href or "")
    return (u.hostname or "").lower(), u.path


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

def open_entry(page):
    """Navigate the target page to the sign-in entry and identify where it landed.
    Page facts: contract §8 row 1 -- ServiceLogin redirects to /v3/signin/accountchooser
    because the profile remembers the account[cite: 1].

    Returns AT_CHOOSER (a rendered chooser item is on screen) or ALREADY_SIGNED_IN (the entry
    bounced straight to voice.google.com, i.e. the session is fine after all)."""
    page.send("Page.enable")
    nav = page.send("Page.navigate", url=ENTRY_URL)
    if nav.get("errorText"):
        raise Unrecognised("entry navigation failed")  # after first send: not TRANSPORT
    page.wait_for("Page.loadEventFired", STEP_TIMEOUT_S)

    def landed():
        try:
            host, path = _host_and_path(page.live_href())
            if host == "voice.google.com":
                return ALREADY_SIGNED_IN
            if (host == "accounts.google.com" and path.startswith("/v3/signin/accountchooser")
                    and is_rendered(page, CHOOSER_ITEM)):
                return AT_CHOOSER
        except Unrecognised:
            pass  # context swapped mid-redirect; poll again within budget
        return None

    state = wait_until(landed, STEP_TIMEOUT_S, "a recognised landing page after the entry URL")
    log(f"entry landed: {state}")
    return state


def choose_account(page, email):
    """OWNER. Get from the account chooser to the password page for `email`.
    Page facts: contract §8 rows 2-3[cite: 1]. If the chooser is absent, shows zero or several matching
    accounts, or the email-entry path appears (never observed), raise Unrecognised."""
    
    # Safely inject the string literal into JS, bypassing CSS selector escaping issues
    click_expr = (
        "(() => {"
        "  const items = document.querySelectorAll('div[role=\"link\"][data-identifier][data-button-type=\"multipleChoiceIdentifier\"]');"
        f" const target = {json.dumps(email.lower())};"
        "  for (const e of items) {"
        "    if (e.getAttribute('data-identifier').toLowerCase() === target) {"
        "      const s = getComputedStyle(e); const r = e.getBoundingClientRect();"
        "      const rendered = s.display !== 'none' && s.visibility !== 'hidden' && r.width > 0 && r.height > 0 && (e.offsetParent !== null || s.position === 'fixed');"
        "      if (rendered) { e.click(); return true; }"
        "    }"
        "  }"
        "  return false;"
        "})()"
    )
    if not page.evaluate(click_expr):
        raise Unrecognised("target account missing from chooser or chooser not rendered")

    def at_pwd():
        host, path = _host_and_path(page.live_href())
        if host == "accounts.google.com" and path.startswith("/v3/signin/challenge/pwd"):
            return is_rendered(page, 'input[type="password"][name="Passwd"]')
        return False

    wait_until(at_pwd, STEP_TIMEOUT_S, "password page to render")

    hidden_email = page.evaluate("document.querySelector('input#hiddenEmail[name=\"identifier\"]').value")
    if not hidden_email or hidden_email.lower() != email.lower():
        raise Unrecognised("password page loaded for wrong email")


def submit_password_once(page, password):
    """OWNER. Enter the password and submit -- EXACTLY ONCE (contract §7.2)[cite: 1].
    Page facts: contract §8 row 4[cite: 1]. Keep `password` out of logs, exceptions, and any
    Runtime.evaluate expression string[cite: 1]."""
    pwd_input = 'input[type="password"][name="Passwd"]' #[cite: 1]
    if not is_rendered(page, pwd_input):
        raise Unrecognised("password input not rendered before submit")

    # Safely focus using evaluate, but inject text using CDP to avoid leaking to eval string[cite: 1]
    page.evaluate(f"document.querySelector({json.dumps(pwd_input)}).focus()")
    page.send("Input.insertText", text=password)

    submit_btn = '#passwordNext' #[cite: 1]
    if not is_rendered(page, submit_btn):
        raise Unrecognised("submit button not rendered before submit")

    page.evaluate(f"document.querySelector({json.dumps(submit_btn)}).click()")


def classify_after_submit(page):
    """OWNER. Wait up to SUBMIT_SETTLE_S for a state you can POSITIVELY identify, then return one
    of SIGNED_IN, CREDENTIAL_REJECTED, CHALLENGED. Anything else -> raise Unrecognised.
    Page facts: contract §8 rows 5 (success), 6 (rejection), 8 + §7.3 (hidden templates)[cite: 1].
    Use live_href() and is_rendered()/rendered_text(); never test presence alone[cite: 1]."""

    def state_settled():
        host, path = _host_and_path(page.live_href())

        # 1. Success - Settled domain (Spike row 5)[cite: 1]
        if host == "voice.google.com":
            return SIGNED_IN

        if host == "accounts.google.com" and path.startswith("/v3/signin/challenge/pwd"):
            
            # 2. Challenged - Must check FIRST before password rejection.
            # Invisible captchas becoming visible (Spike §7.3 & row 8)[cite: 1]
            if is_rendered(page, '#ca') or is_rendered(page, 'img#captchaimg'): #[cite: 1]
                return CHALLENGED

            # Check explicit "Too many failed attempts" lockout in aria-live="assertive"[cite: 1]
            lockout_text = rendered_text(page, '[aria-live="assertive"]')
            if lockout_text and "Too many failed attempts" in lockout_text:
                return CHALLENGED

            # 3. Rejection - Wrong password state (Spike row 6)[cite: 1]
            # Stricter evaluation checking BOTH aria-invalid and rendered #c0 text[cite: 1]
            if is_rendered(page, 'input[name="Passwd"][aria-invalid="true"]'): #[cite: 1]
                c0_text = rendered_text(page, '#c0')
                if c0_text and "Wrong password" in c0_text:
                    return CREDENTIAL_REJECTED

        return None

    return wait_until(state_settled, SUBMIT_SETTLE_S, "a recognised settled state after submit")


# --- main: contract plumbing ---------------------------------------------------------------
def run(fields):
    ws_url = find_target_ws_url(fields["cdp_port"], fields["target_id"])  # TRANSPORT-legal[cite: 1]
    page = Page(ws_url)                                                     # TRANSPORT-legal[cite: 1]
    try:
        try:
            state = open_entry(page)
            # Short-circuit logic if session was secretly alive already
            if state == ALREADY_SIGNED_IN:
                return SIGNED_IN
                
            choose_account(page, fields["email"])
            submit_password_once(page, fields["password"])
            return classify_after_submit(page)
        except Transport:
            # A Transport raised after the first send is not legal as TRANSPORT (§5.1)[cite: 1].
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