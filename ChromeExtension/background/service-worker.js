// RotaryPhone GV Bridge — Background Service Worker (minimal)
//
// The WebSocket connection now lives in the content script (gv-bridge.js)
// because MV3 service workers get suspended after ~30s, killing WebSockets.
// This service worker only handles extension lifecycle events.

let gvTabId = null;

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

console.log('[GVBridge] Service worker initialized (WebSocket handled by content script)');
