# Known Issues

## GV BYE not terminating calls (2026-05-25)

**Status:** Workaround in place (SRTP media teardown forces Google timeout)
**Impact:** When hanging up the rotary phone, the cell phone call ends after ~5-10 seconds (Google media timeout) instead of immediately.
**Root cause:** Our SIP BYE over WebSocket is structurally correct but Google's SIP proxy silently ignores it. Likely a dialog state mismatch (From/To tags, Contact URI, or CSeq) that requires proper SIP tracing to diagnose.
**Workaround:** On hangup, `GvSipTransport.HangupAsync()` now closes the DTLS-SRTP `RTCPeerConnection` immediately (sending `close_notify`) before sending the SIP BYE. This stops all media flow, and Google's RTP timeout detection terminates the far-end call within 5-10 seconds.
**Next step:** Set up WebSocket frame capture to compare our BYE with what Google's own web client sends when terminating a call. Compare headers field-by-field.
**PRs:** #25 (initial BYE), #26 (CSeq fix), #27 (diagnostic logging), #28 (session race fix), #29 (force media teardown)
