// RotaryPhone GV Bridge — Content Script
// Injected into voice.google.com to observe DOM, control calls,
// and maintain a persistent WebSocket to the .NET GVBridgeService.
//
// The WebSocket lives here (not in the service worker) because
// MV3 service workers get suspended after ~30s, killing WebSockets.
// Content scripts persist as long as the page is open.

'use strict';

const WS_URL = 'ws://127.0.0.1:8765';
let ws = null;
let backoff = 1000;

// Stable ARIA selectors — update only this block if GV DOM changes
const SELECTORS = {
  newCallButton:     'button[aria-label="New call"]',
  numberInput:       'input[aria-label="Type a number"]',
  dialButton:        'button[aria-label="Call"]',
  answerButton:      'button[aria-label="Answer"]',
  hangupButton:      'button[aria-label="End call"], button[aria-label="Hang up"]',
  incomingDialog:    '[role="dialog"]',
  messageInput:      'textarea[aria-label="Message"]',
  sendButton:        'button[aria-label="Send message"]',
  callDurationTimer: '[data-call-duration], [aria-label*="call duration"]',
};

let callActive = false;
let incomingDetected = false;

// --- WebSocket Connection ---

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
    return;
  }

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

function sendToServer(msg) {
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(msg));
  } else {
    console.warn('[GVBridge] Cannot send — not connected to bridge');
  }
}

function handleBridgeMessage(msg) {
  switch (msg.type) {
    case 'dial':
      dial(msg.number);
      break;
    case 'answer':
      answer();
      break;
    case 'hangup':
      hangup();
      break;
    case 'sendSms':
      sendSms(msg.to, msg.body);
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

// --- DOM Observation ---

function setupObserver() {
  const observer = new MutationObserver(handleMutations);
  observer.observe(document.body, { childList: true, subtree: true });
  console.log('[GVBridge] DOM observer started');
}

function handleMutations(mutations) {
  // Check for incoming call dialog
  const dialogs = document.querySelectorAll(SELECTORS.incomingDialog);
  for (const dialog of dialogs) {
    const label = dialog.getAttribute('aria-label') || dialog.textContent || '';
    if (/incoming call/i.test(label) && !incomingDetected) {
      incomingDetected = true;
      const callerInfo = extractCallerFromDialog(dialog);
      sendToServer({
        type: 'incomingCall',
        from: callerInfo || 'Unknown',
        callId: `gv-${Date.now()}`
      });
    }
  }

  // Check for call duration timer (call answered)
  const timer = document.querySelector(SELECTORS.callDurationTimer);
  if (timer && incomingDetected && !callActive) {
    callActive = true;
    incomingDetected = false;
    sendToServer({ type: 'callAnswered', callId: `gv-${Date.now()}` });
  }

  // Check for call ended (timer disappeared)
  if (!timer && callActive) {
    callActive = false;
    sendToServer({ type: 'callEnded', callId: `gv-${Date.now()}` });
  }
}

function extractCallerFromDialog(dialog) {
  const text = dialog.textContent || '';
  const phoneMatch = text.match(/(\+?\d[\d\s\-().]{6,})/);
  return phoneMatch ? phoneMatch[1].trim() : text.substring(0, 50).trim();
}

// --- Call Control Actions ---

function retryAction(action, maxRetries = 3, delayMs = 200) {
  let attempts = 0;
  const tryAction = () => {
    attempts++;
    try {
      if (action()) return true;
    } catch (e) {
      console.warn(`[GVBridge] Action attempt ${attempts} failed:`, e);
    }
    if (attempts < maxRetries) {
      setTimeout(tryAction, delayMs);
    } else {
      console.error('[GVBridge] Action failed after', maxRetries, 'attempts');
    }
    return false;
  };
  return tryAction();
}

function dial(number) {
  retryAction(() => {
    const newCallBtn = document.querySelector(SELECTORS.newCallButton);
    if (!newCallBtn) return false;
    newCallBtn.click();

    setTimeout(() => {
      const input = document.querySelector(SELECTORS.numberInput);
      if (input) {
        input.value = number;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        setTimeout(() => {
          const callBtn = document.querySelector(SELECTORS.dialButton);
          if (callBtn) callBtn.click();
        }, 300);
      }
    }, 500);
    return true;
  });
}

function answer() {
  retryAction(() => {
    const btn = document.querySelector(SELECTORS.answerButton);
    if (!btn) return false;
    btn.click();
    return true;
  });
}

function hangup() {
  retryAction(() => {
    const btn = document.querySelector(SELECTORS.hangupButton);
    if (!btn) return false;
    btn.click();
    callActive = false;
    incomingDetected = false;
    return true;
  });
}

function sendSms(to, body) {
  console.log('[GVBridge] sendSms not fully implemented — requires thread navigation');
}

// --- fetch() interceptor for SMS data ---

const _fetch = window.fetch;
window.fetch = async function(url, opts) {
  const resp = await _fetch.apply(this, arguments);

  try {
    if (typeof url === 'string' && url.includes('/voice/v1/voiceclient/conversation')) {
      resp.clone().json().then(data => {
        if (data?.conversation?.message) {
          for (const msg of data.conversation.message) {
            if (msg.type === 'INCOMING_TEXT') {
              sendToServer({
                type: 'smsReceived',
                from: msg.sender || 'Unknown',
                body: msg.text || '',
                threadId: data.conversation.id || ''
              });
            }
          }
        }
      }).catch(() => {});
    }
  } catch (e) {
    // Don't break GV's own fetch flow
  }

  return resp;
};

// --- Init ---
setupObserver();
connect();
console.log('[GVBridge] Content script loaded on', window.location.href);
