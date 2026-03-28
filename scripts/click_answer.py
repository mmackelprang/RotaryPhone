#!/usr/bin/env python3
"""Click the Answer button on the Google Voice page via CDP."""
import asyncio, json, websockets, urllib.request, sys

async def click_answer():
    tabs = json.loads(urllib.request.urlopen("http://localhost:9224/json").read())
    for t in tabs:
        if "voice.google.com" in t.get("url", "") and t.get("type") == "page":
            async with websockets.connect(t["webSocketDebuggerUrl"]) as ws:
                await ws.send(json.dumps({"id": 1, "method": "Runtime.evaluate", "params": {
                    "expression": """
                        (function() {
                            var btns = document.querySelectorAll('button');
                            for (var i = 0; i < btns.length; i++) {
                                var label = (btns[i].getAttribute('aria-label') || btns[i].innerText || '').toLowerCase();
                                if (/\\banswer\\b|\\baccept\\b/.test(label)) {
                                    btns[i].click();
                                    return 'clicked: ' + label;
                                }
                            }
                            return 'no answer button found';
                        })()
                    """
                }}))
                msg = await asyncio.wait_for(ws.recv(), timeout=5)
                result = json.loads(msg).get("result",{}).get("result",{}).get("value","")
                print(result)
            return
    print("no voice.google.com tab")

asyncio.run(click_answer())
