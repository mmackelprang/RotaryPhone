# Signaler Subscription Format — Investigation Notes

**Status:** Protocol working, subscription data format needs refinement.
**Last updated:** 2026-03-28

## What Works

The 3-step signaler protocol (chooseServer → bind → poll) is verified working.
The long-poll returns events (got `noop` heartbeat). See `signaler-protocol.md`.

## What Doesn't Work

Some subscriptions return `INVALID_ARGUMENT`:
```
{"error":{"code":400,"details":[{"@type":"type.googleapis.com/google.rpc.BadRequest",
"fieldViolations":[{"description":"Invalid JSON payload received. Unknown name \"\":
Root element must be a message."}]}],
"message":"Invalid JSON payload received. Unknown name \"\": Root element must be a message.",
"status":"INVALID_ARGUMENT"}}
```

Followed by `["close"]` which terminates the channel.

## Current Subscription Format

6 subscriptions sent in the bind:
```
req0: [[[1,[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]],null,null,1],null,3]]]
req1: [[[2,[null,null,null,[9,5],null,[null,[null,1],[[["3"]]]],null,null,1],null,3]]]
req2: [[[3,[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]],null,null,1],null,3]]]
req3: [[[4,[null,null,null,[9,5],null,[null,[null,1],[[["1"]]]],null,null,1],null,3]]]
req4: [[[5,[null,null,null,[9,5],null,[null,[null,1],[[["2"]]]],null,null,1],null,3]]]
req5: [[[6,[null,null,null,[9,5],null,[null,[null,1],[[["3"]]]],null,null,1],null,3]]]
```

## Key Finding (2026-03-28)

The channel SURVIVES after the subscription error. Poll 2 returns `noop` heartbeats.
The `close` events close individual subscriptions, not the whole channel.
But without working subscriptions, no GV events are delivered.

The browser sends the EXACT SAME subscription format (verified via Playwright interception)
and it works for the browser but not for our curl/Python requests. This suggests the
subscription processing on Google's backend checks something about the session context
(perhaps a session cookie like COMPASS, or the user agent, or the `zx` cache buster).

## Alternative Approach: Playwright-as-Signaler

Instead of reverse-engineering the subscription format, use Playwright headless as the
signaler client. The browser handles the protocol natively, and we intercept events
by evaluating JavaScript in the page context. This is Phase 1 compatible (browser
still needed for audio anyway) and gives us working incoming call detection immediately.

## Next Steps

1. **Capture a WORKING subscription from the browser** — use the Playwright capture script
   with non-headless mode and wait for the page to fully load with signaler connected.
   The current capture shows the browser making the same format, so the issue might be
   timing (PSIDTS expired during the capture) rather than format.

2. **Try fewer subscriptions** — start with `count=1` and a single subscription to isolate
   which ones fail.

3. **Try with the browser's exact gsessionid** — if a prior session's gsessionid matters,
   try reusing one from a successful browser session.

4. **Check if the subscription data needs account-specific values** — the inner values
   `["1"]`, `["3"]`, `["2"]` might need to be replaced with account/device IDs.

5. **Monitor the Playwright browser's console** — the GV JavaScript might log subscription
   details that reveal the expected format.
