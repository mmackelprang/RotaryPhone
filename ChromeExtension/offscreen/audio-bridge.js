// RotaryPhone GV Bridge — Offscreen Audio Bridge
// Handles tabCapture and PCM relay (placeholder for Phase 2 audio implementation)

'use strict';

console.log('[GVBridge Audio] Offscreen document loaded');

// Audio bridging will be implemented when tabCapture and WebRTC
// hook integration is ready. This file serves as the scaffold.

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === 'startCapture') {
    console.log('[GVBridge Audio] Audio capture requested (not yet implemented)');
    // Future: chrome.tabCapture.capture() + AudioContext processing
  } else if (msg.type === 'stopCapture') {
    console.log('[GVBridge Audio] Audio capture stop requested');
  } else if (msg.type === 'audioFrame') {
    // Future: inject PCM into MediaStream for outbound audio
  }
  sendResponse({ ok: true });
  return false;
});
