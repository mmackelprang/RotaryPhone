"""
Capture the exact signaler protocol from a live voice.google.com session.
Uses Playwright to load the page with existing cookies and intercept all
signaler-pa.clients6.google.com traffic.

Usage: python scripts/capture-signaler.py
Then call +19196706660 from another phone to trigger incoming call events.
"""
import json, sys, time, os

try:
    from playwright.sync_api import sync_playwright
except ImportError:
    print("Installing playwright...")
    os.system(f"{sys.executable} -m pip install playwright")
    os.system(f"{sys.executable} -m playwright install chromium")
    from playwright.sync_api import sync_playwright

# Cookies from the user's session
COOKIES = [
    {"name": "SID", "value": "g.a0007wgann76P6MiANhu7PXKLjAUMYespStaNjGv-QTvmlFiSxGTgtRVz04jjmBpA_3abcsy-QACgYKASoSARYSFQHGX2MizaEm564mTa-nO9PPP4F2xxoVAUF8yKqMRyGHPj8Gv-MfbwE1XAaY0076", "domain": ".google.com", "path": "/"},
    {"name": "HSID", "value": "AFYo3TEwe4VbBOgpU", "domain": ".google.com", "path": "/"},
    {"name": "SSID", "value": "Aq1ArexH3ikWjmApm", "domain": ".google.com", "path": "/", "secure": True},
    {"name": "APISID", "value": "8yNPM4Xsqi16z_f9/A4pAZAC82BlYoAhyL", "domain": ".google.com", "path": "/"},
    {"name": "SAPISID", "value": "IEqRTt8hN-XGbqNn/Akk11R_mDRGnHq0tA", "domain": ".google.com", "path": "/", "secure": True},
    {"name": "__Secure-1PSID", "value": "g.a0007wgann76P6MiANhu7PXKLjAUMYespStaNjGv-QTvmlFiSxGTcCy2hnS2povqjqSGjznnDwACgYKAfASARYSFQHGX2MinPaNg4YiVYk6xpMXo8-VexoVAUF8yKrvVhpr2D8TFHCB003jF9ET0076", "domain": ".google.com", "path": "/", "secure": True, "httpOnly": True},
    {"name": "__Secure-3PSID", "value": "g.a0007wgann76P6MiANhu7PXKLjAUMYespStaNjGv-QTvmlFiSxGTmCb-M2wMAFE7eTA5WX98eAACgYKARQSARYSFQHGX2Mio8Oo-Wtr6OJB_jBsNJ2PwhoVAUF8yKomQKevREceLVv5637cVENK0076", "domain": ".google.com", "path": "/", "secure": True, "httpOnly": True},
    {"name": "__Secure-1PAPISID", "value": "IEqRTt8hN-XGbqNn/Akk11R_mDRGnHq0tA", "domain": ".google.com", "path": "/", "secure": True},
    {"name": "__Secure-3PAPISID", "value": "IEqRTt8hN-XGbqNn/Akk11R_mDRGnHq0tA", "domain": ".google.com", "path": "/", "secure": True},
    {"name": "__Secure-1PSIDTS", "value": "sidts-CjcBWhotCVxBUb2AAOv-HlNspfn0QaylwDDM1QS38TE5RMcVXNlacnMmIWIQP8R1gx866pMZzOB5EAA", "domain": ".google.com", "path": "/", "secure": True, "httpOnly": True},
    {"name": "__Secure-3PSIDTS", "value": "sidts-CjcBWhotCVxBUb2AAOv-HlNspfn0QaylwDDM1QS38TE5RMcVXNlacnMmIWIQP8R1gx866pMZzOB5EAA", "domain": ".google.com", "path": "/", "secure": True, "httpOnly": True},
    {"name": "__Secure-1PSIDCC", "value": "AKEyXzWsGiLzJXXrsEg3SLddjtREm2nVVOXhpBJbax9b66E4qtO0LPLbPpraiphhhT5-JDxWFTNn", "domain": ".google.com", "path": "/", "secure": True, "httpOnly": True},
    {"name": "__Secure-3PSIDCC", "value": "AKEyXzUGi5y5uU9drehVxshmS3b4RI7yOcVuJHAK2kWHvmNsjlHbPf-jjNDZ3-1fpXu5-jFI8aM4", "domain": ".google.com", "path": "/", "secure": True, "httpOnly": True},
]

captured = []

def on_request(request):
    url = request.url
    if 'signaler' in url or 'punctual' in url:
        method = request.method
        post = request.post_data or ""
        headers = {k: v for k, v in request.headers.items() if k.lower() in ('authorization', 'cookie', 'content-type', 'x-goog-authuser')}

        entry = {
            "timestamp": time.time(),
            "method": method,
            "url": url,
            "post_data_length": len(post),
            "post_data": post[:2000] if post else None,
            "headers": headers,
        }
        captured.append(entry)

        short_url = url.split('?')[0].split('/')[-1] if '?' in url else url[-50:]
        print(f"[{method}] {short_url} ({len(post)}b post)")
        if post:
            # Parse form data
            from urllib.parse import parse_qs
            params = parse_qs(post, keep_blank_values=True)
            for k, v in sorted(params.items()):
                if k.startswith('req'):
                    print(f"  {k}: {v[0][:100]}")
                else:
                    print(f"  {k}: {v[0][:50]}")

def on_response(response):
    url = response.url
    if 'signaler' in url or 'punctual' in url:
        status = response.status
        try:
            body = response.text()
        except:
            body = "(could not read)"

        print(f"  -> {status} ({len(body)}b)")
        if body and len(body) < 2000:
            print(f"  -> {body[:500]}")

        # Update the last captured entry
        for entry in reversed(captured):
            if entry["url"] == url and "response" not in entry:
                entry["response_status"] = status
                entry["response_body"] = body[:5000]

                # Extract cookies from response
                resp_headers = response.headers
                if 'set-cookie' in resp_headers:
                    entry["set_cookies"] = resp_headers['set-cookie']
                break

print("="*60)
print("SIGNALER PROTOCOL CAPTURE")
print("="*60)
print("Loading voice.google.com with your cookies...")
print("Once loaded, CALL +19196706660 to capture incoming call events.")
print("Press Ctrl+C to stop and save capture.")
print("="*60)
print()

with sync_playwright() as p:
    browser = p.chromium.launch(headless=False)
    context = browser.new_context()
    context.add_cookies(COOKIES)

    page = context.new_page()
    page.on("request", on_request)
    page.on("response", on_response)

    page.goto("https://voice.google.com/u/0/calls")
    print("\nPage loaded. Waiting for signaler activity...\n")

    try:
        # Wait for signaler requests to appear (up to 2 min)
        page.wait_for_timeout(120000)
    except KeyboardInterrupt:
        pass

    # Save capture
    outfile = "data/signaler-capture.json"
    os.makedirs("data", exist_ok=True)
    with open(outfile, "w") as f:
        json.dump(captured, f, indent=2)
    print(f"\nCapture saved to {outfile} ({len(captured)} requests)")

    browser.close()
