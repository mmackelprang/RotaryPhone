#!/usr/bin/env python3
"""Extract Google Voice cookies from running Chromium via CDP HTTP API.
No Playwright, no WebSocket — pure HTTP to Chrome DevTools Protocol.

Usage:
  1. Ensure Chromium is running with --remote-debugging-port (9222 or 9224)
  2. Be logged into voice.google.com in that browser
  3. Run: python3 gv-extract-cookies.py [port]
"""
import json, urllib.request, os, sys, hashlib, time

CDP_PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9224
CDP_URL = f"http://127.0.0.1:{CDP_PORT}"
COOKIE_PATH = "data/gv-cookies.enc"
KEY_PATH = "data/gv-key.bin"
API_KEY = "AIzaSyDTYc1N4xiODyrQYK0Kl6g_y279LjYkrBg"

def cdp_send(ws_debug_url, method, params=None):
    """Send CDP command via HTTP endpoint (avoids WebSocket origin issues)."""
    # Use the /json/protocol endpoint doesn't help. Instead, use
    # the page's /json endpoint to find a page, then use CDP via
    # a direct HTTP POST to the browser endpoint.
    pass

def main():
    print(f"Connecting to Chromium CDP on port {CDP_PORT}...")

    # Get list of pages
    try:
        raw = urllib.request.urlopen(f"{CDP_URL}/json").read()
        pages = json.loads(raw)
    except Exception as e:
        print(f"ERROR: Cannot connect to Chromium at {CDP_URL}: {e}")
        print(f"Start Chromium with: chromium-browser --remote-debugging-port={CDP_PORT}")
        sys.exit(1)

    # Find GV page
    gv_page = next(
        (p for p in pages
         if "voice.google.com" in p.get("url", "") and p.get("type") == "page"),
        None)

    if not gv_page:
        print("No voice.google.com page found. Log in first.")
        for p in pages:
            print(f"  {p.get('type', '?')}: {p.get('url', '?')[:60]}")
        sys.exit(1)

    page_url = gv_page["url"]
    print(f"Found GV page: {page_url[:70]}")

    # Use JavaScript evaluation via CDP to get cookies
    # The /json/evaluate endpoint doesn't exist, so we need WebSocket.
    # Instead, use a workaround: fetch cookies via the browser's /json/version
    # and then use the page's document.cookie + Network.getAllCookies via WS
    # with the correct origin header.

    ws_url = gv_page.get("webSocketDebuggerUrl", "")
    if not ws_url:
        print("ERROR: No debugger URL available")
        sys.exit(1)

    # Try WebSocket with correct origin
    try:
        import websocket
        ws = websocket.create_connection(ws_url, origin=f"http://127.0.0.1:{CDP_PORT}")
    except Exception:
        # Try with wildcard origin
        try:
            import websocket
            ws = websocket.create_connection(ws_url, header={"Origin": f"http://127.0.0.1:{CDP_PORT}"})
        except Exception as e:
            print(f"WebSocket connection failed: {e}")
            print("Try restarting Chromium with: --remote-allow-origins=*")
            sys.exit(1)

    # Get all cookies
    ws.send(json.dumps({"id": 1, "method": "Network.getAllCookies"}))
    resp = json.loads(ws.recv())
    ws.close()

    cookies = resp.get("result", {}).get("cookies", [])
    google_cookies = [c for c in cookies if ".google.com" in c.get("domain", "")]
    cookie_header = "; ".join(f'{c["name"]}={c["value"]}' for c in google_cookies)

    sapisid = next((c["value"] for c in google_cookies if c["name"] == "SAPISID"), "")
    if not sapisid:
        print("ERROR: SAPISID not found. Are you logged in?")
        sys.exit(1)

    print(f"Got {len(google_cookies)} google cookies, SAPISID: {sapisid[:10]}...")

    # Build cookie set JSON
    cookie_set = json.dumps({
        "Sapisid": sapisid,
        "Sid": next((c["value"] for c in google_cookies if c["name"] == "SID"), ""),
        "Hsid": next((c["value"] for c in google_cookies if c["name"] == "HSID"), ""),
        "Ssid": next((c["value"] for c in google_cookies if c["name"] == "SSID"), ""),
        "Apisid": next((c["value"] for c in google_cookies if c["name"] == "APISID"), ""),
        "Secure1Psid": next((c["value"] for c in google_cookies if c["name"] == "__Secure-1PSID"), ""),
        "Secure3Psid": next((c["value"] for c in google_cookies if c["name"] == "__Secure-3PSID"), ""),
        "RawCookieHeader": cookie_header,
    })

    # AES-GCM encrypt (matches TokenEncryption.cs: nonce(12) || tag(16) || ciphertext)
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM

    key = os.urandom(32)
    nonce = os.urandom(12)
    ct_with_tag = AESGCM(key).encrypt(nonce, cookie_set.encode(), None)
    # AESGCM returns ciphertext || tag concatenated
    tag = ct_with_tag[-16:]
    ct = ct_with_tag[:-16]
    encrypted = nonce + tag + ct

    os.makedirs(os.path.dirname(COOKIE_PATH) or ".", exist_ok=True)
    with open(COOKIE_PATH, "wb") as f:
        f.write(encrypted)
    with open(KEY_PATH, "wb") as f:
        f.write(key)
    print(f"Saved {COOKIE_PATH} ({len(encrypted)}b) and {KEY_PATH}")

    # Health check
    print("\nVerifying with GV API...")
    ts = int(time.time())
    sha1 = hashlib.sha1(f"{ts} {sapisid} https://voice.google.com".encode()).hexdigest()
    req = urllib.request.Request(
        f"https://clients6.google.com/voice/v1/voiceclient/threadinginfo/get?alt=protojson&key={API_KEY}",
        data=b"[]",
        headers={
            "Authorization": f"SAPISIDHASH {ts}_{sha1}",
            "Cookie": cookie_header,
            "Origin": "https://voice.google.com",
            "Referer": "https://voice.google.com/",
            "Content-Type": "application/json+protobuf",
            "X-Goog-AuthUser": "0",
        })

    try:
        r = urllib.request.urlopen(req)
        body = r.read().decode()
        print(f"Health check: {r.status} OK")
        print(f"Response: {body[:200]}")
        print("\nCookies valid! Restart rotary-phone: sudo systemctl restart rotary-phone")
    except urllib.error.HTTPError as e:
        print(f"Health check failed: {e.code} {e.reason}")

if __name__ == "__main__":
    main()
