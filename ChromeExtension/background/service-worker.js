// RotaryPhone GV Bridge — Background Service Worker (minimal)
//
// The WebSocket connection now lives in the content script (gv-bridge.js)
// because MV3 service workers get suspended after ~30s, killing WebSockets.
// This service worker handles extension lifecycle events, tabCapture, and
// offscreen document management.

let gvTabId = null;
let offscreenCreated = false;

// Track the Google Voice tab
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (tab.url?.includes('voice.google.com')) {
    gvTabId = tabId;
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  if (tabId === gvTabId) {
    gvTabId = null;
  }
});

// --- Offscreen document management ---

async function ensureOffscreen() {
  if (offscreenCreated) return;

  // Check if one already exists (e.g. after service worker restart)
  const existingContexts = await chrome.runtime.getContexts({
    contextTypes: ['OFFSCREEN_DOCUMENT'],
  });
  if (existingContexts.length > 0) {
    offscreenCreated = true;
    return;
  }

  await chrome.offscreen.createDocument({
    url: 'offscreen/offscreen.html',
    reasons: ['USER_MEDIA'],
    justification: 'Capture tab audio for GV call',
  });
  offscreenCreated = true;
  console.log('[GVBridge] Offscreen document created');
}

// --- Message handler ---

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  switch (msg.type) {
    case 'requestTabCapture': {
      const targetTabId = msg.tabId || gvTabId;
      if (!targetTabId) {
        sendResponse({ ok: false, error: 'No Google Voice tab found' });
        return false;
      }
      chrome.tabCapture.getMediaStreamId(
        { targetTabId: targetTabId },
        (streamId) => {
          if (chrome.runtime.lastError) {
            console.error('[GVBridge] tabCapture error:', chrome.runtime.lastError.message);
            sendResponse({ ok: false, error: chrome.runtime.lastError.message });
          } else {
            console.log('[GVBridge] tabCapture streamId obtained');
            sendResponse({ ok: true, streamId: streamId });
          }
        }
      );
      return true; // async sendResponse
    }

    case 'createOffscreen':
      ensureOffscreen()
        .then(() => sendResponse({ ok: true }))
        .catch((err) => {
          console.error('[GVBridge] Failed to create offscreen doc:', err);
          sendResponse({ ok: false, error: err.message });
        });
      return true; // async sendResponse

    case 'audioFrame':
      // Relay audioFrame from offscreen doc to the content script (GV tab)
      if (gvTabId) {
        chrome.tabs.sendMessage(gvTabId, {
          type: 'audioFrame',
          pcm: msg.pcm,
          direction: 'capture', // captured from tab → send to bridge server
        });
      }
      sendResponse({ ok: true });
      return false;

    case 'postCallEvent':
      // Relay call event to RotaryPhone server via HTTP POST.
      // Service workers can fetch localhost without mixed-content restrictions.
      fetch('http://127.0.0.1:5004/api/gvbridge/event', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(msg.event)
      }).then(r => {
        console.log('[GVBridge] HTTP POST:', msg.event.type, r.status);
        sendResponse({ ok: true, status: r.status });
      }).catch(e => {
        console.error('[GVBridge] HTTP POST failed:', e);
        sendResponse({ ok: false, error: e.message });
      });
      return true; // async sendResponse

    default:
      return false;
  }
});

console.log('[GVBridge] Service worker initialized (WebSocket handled by content script)');
