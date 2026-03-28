#!/usr/bin/env python3
"""Debug the Google Voice page via CDP to check button state and extension status."""
import asyncio, json, websockets, urllib.request

async def debug():
    tabs = json.loads(urllib.request.urlopen("http://localhost:9224/json").read())
    for t in tabs:
        if "voice.google.com" in t.get("url", "") and t.get("type") == "page":
            async with websockets.connect(t["webSocketDebuggerUrl"]) as ws:
                queries = [
                    ("Call buttons",
                     'Array.from(document.querySelectorAll("button")).map(function(b) {'
                     '  var label = b.getAttribute("aria-label") || "";'
                     '  var text = (b.innerText || "").trim().substring(0,30);'
                     '  var vis = b.offsetHeight > 0;'
                     '  return label + " | " + text + " | visible=" + vis;'
                     '}).filter(function(x) { return /answer|accept|call|end|mute|hold|decline/i.test(x); }).join("\\n") || "no call buttons"'),
                    ("Answer flag", 'document.documentElement.getAttribute("data-gvbridge-answer") || "not set"'),
                    ("Poll state", 'document.documentElement.getAttribute("data-gvbridge-poll") || "not set"'),
                    ("Page title", "document.title"),
                    ("Notification permission", "Notification.permission"),
                ]

                for i, (label, expr) in enumerate(queries):
                    await ws.send(json.dumps({"id": i+1, "method": "Runtime.evaluate", "params": {"expression": expr}}))
                    msg = await asyncio.wait_for(ws.recv(), timeout=5)
                    val = json.loads(msg).get("result",{}).get("result",{}).get("value","")
                    if label == "Poll state" and "|" in val:
                        parts = val.split("|")
                        val = " ".join(parts[1:])
                    print(f"{label}: {val}")

                # Check content script contexts
                await ws.send(json.dumps({"id": 10, "method": "Runtime.enable"}))
                contexts = []
                for _ in range(10):
                    try:
                        msg = await asyncio.wait_for(ws.recv(), timeout=2)
                        data = json.loads(msg)
                        if data.get("method") == "Runtime.executionContextCreated":
                            ctx = data["params"]["context"]
                            name = ctx.get("name", "")
                            if name:
                                contexts.append(f"  {ctx['id']}: {name}")
                    except asyncio.TimeoutError:
                        break
                print(f"Extension contexts: {len(contexts)}")
                for c in contexts:
                    print(c)
            break

asyncio.run(debug())
