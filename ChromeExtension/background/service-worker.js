// RotaryPhone GV Bridge — Background Service Worker
// Manages WebSocket connection to the .NET GVBridgeService

const WS_URL = 'ws://127.0.0.1:8765';
let ws = null;
let backoff = 1000;
let gvTabId = null;

function connect() {
  try {
    ws = new WebSocket(WS_URL);
  } catch (e) {
    console.error('[GVBridge] WebSocket creation failed:', e);
    scheduleReconnect();
    return;
  }

  ws.onopen = () => {
    console.log('[GVBridge] Connected to bridge server');
    backoff = 1000;
    ws.send(JSON.stringify({ type: 'connected', version: '1.0.0' }));
  };

  ws.onclose = () => {
    console.log('[GVBridge] Disconnected from bridge server');
    ws = null;
    scheduleReconnect();
  };

  ws.onerror = (e) => {
    console.error('[GVBridge] WebSocket error:', e);
  };

  ws.onmessage = ({ data }) => {
    try {
      const msg = JSON.parse(data);
      handleBridgeMessage(msg);
    } catch (e) {
      console.error('[GVBridge] Error parsing message:', e);
    }
  };
}

function scheduleReconnect() {
  const delay = Math.min(backoff, 30000);
  console.log(`[GVBridge] Reconnecting in ${delay}ms...`);
  setTimeout(connect, delay);
  backoff = Math.min(backoff * 2, 30000);
}

function handleBridgeMessage(msg) {
  switch (msg.type) {
    case 'dial':
    case 'answer':
    case 'hangup':
    case 'sendSms':
      // Forward to content script
      if (gvTabId) {
        chrome.tabs.sendMessage(gvTabId, msg).catch(err => {
          console.warn('[GVBridge] Failed to send to content script:', err);
        });
      } else {
        console.warn('[GVBridge] No GV tab available for command:', msg.type);
      }
      break;

    case 'ping':
      if (ws?.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'pong' }));
      }
      break;

    default:
      console.log('[GVBridge] Unknown bridge message:', msg.type);
  }
}

// Listen for messages from content script
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  // Track the GV tab
  if (sender.tab?.url?.includes('voice.google.com')) {
    gvTabId = sender.tab.id;
  }

  // Forward to bridge server
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(msg));
  } else {
    console.warn('[GVBridge] Cannot forward — not connected to bridge');
  }

  sendResponse({ ok: true });
  return false;
});

// Clean up tab reference when tab closes
chrome.tabs.onRemoved.addListener((tabId) => {
  if (tabId === gvTabId) {
    gvTabId = null;
  }
});

// Start connection
connect();
