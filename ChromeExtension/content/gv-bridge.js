// RotaryPhone GV Bridge — Content Script
// Injected into voice.google.com to observe DOM, control calls,
// and maintain a persistent WebSocket to the .NET GVBridgeService.
//
// Only runs in the top frame to prevent duplicate connections.
if (window !== window.top) {
  // Skip iframes
  throw new Error('[GVBridge] Skipping iframe');
}
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
let audioCaptureActive = false;

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

  ws.onclose = (e) => {
    console.log('[GVBridge] Disconnected from bridge server, code:', e.code);
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
    console.log('[GVBridge] Sending via WS:', msg.type);
    ws.send(JSON.stringify(msg));
  } else {
    console.warn('[GVBridge] WS not connected, ws:', ws?.readyState);
  }
}

// HTTP POST via service worker — content scripts on HTTPS pages can't fetch HTTP localhost
// (mixed content), so we relay through the service worker which has no such restriction.
function sendViaHttp(msg) {
  chrome.runtime.sendMessage({ type: 'postCallEvent', event: msg }).then(r => {
    console.log('[GVBridge] HTTP POST via SW:', msg.type, r?.ok ? 'OK' : r?.error);
  }).catch(e => {
    console.error('[GVBridge] HTTP POST relay failed:', e);
  });
}

// --- Audio Bridge Control ---

async function startAudioCapture() {
  if (audioCaptureActive) {
    console.log('[GVBridge] Audio capture already active');
    return;
  }

  try {
    // 1. Request tabCapture streamId from service worker
    console.log('[GVBridge] Requesting tabCapture streamId...');
    const captureResp = await chrome.runtime.sendMessage({ type: 'requestTabCapture' });
    if (!captureResp?.ok) {
      console.error('[GVBridge] Failed to get tabCapture streamId:', captureResp?.error);
      return;
    }
    const streamId = captureResp.streamId;
    console.log('[GVBridge] Got tabCapture streamId');

    // 2. Ensure offscreen document exists
    console.log('[GVBridge] Ensuring offscreen document...');
    const offscreenResp = await chrome.runtime.sendMessage({ type: 'createOffscreen' });
    if (!offscreenResp?.ok) {
      console.error('[GVBridge] Failed to create offscreen doc:', offscreenResp?.error);
      return;
    }

    // 3. Start capture in offscreen document
    console.log('[GVBridge] Starting audio capture in offscreen document...');
    const startResp = await chrome.runtime.sendMessage({
      type: 'startCapture',
      streamId: streamId,
    });
    if (!startResp?.ok) {
      console.error('[GVBridge] Failed to start capture:', startResp?.error);
      return;
    }

    audioCaptureActive = true;
    console.log('[GVBridge] Audio capture pipeline active');
  } catch (err) {
    console.error('[GVBridge] Error starting audio capture:', err);
  }
}

async function stopAudioCapture() {
  if (!audioCaptureActive) return;

  try {
    await chrome.runtime.sendMessage({ type: 'stopCapture' });
    console.log('[GVBridge] Audio capture stopped');
  } catch (err) {
    console.error('[GVBridge] Error stopping audio capture:', err);
  }
  audioCaptureActive = false;
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
    case 'audioFrame':
      // Inbound audio from bridge server → forward to offscreen for playback
      chrome.runtime.sendMessage({
        type: 'audioFrame',
        pcm: msg.pcm,
        direction: 'playback',
      });
      break;
    default:
      console.log('[GVBridge] Unknown bridge message:', msg.type);
  }
}

// --- DOM Observation (disabled — replaced by polling in startCallPolling) ---
function setupObserver() {
  // No-op: call detection is now handled by startCallPolling()
  // The old MutationObserver matched "Incoming call" from call history, causing false positives.
  console.log('[GVBridge] DOM observer disabled (using polling instead)');
}

function extractCallerInfo() {
  // GV shows caller info in the panel that replaced the dial pad.
  // Format: "Name Google Voice (xxx) xxx-xxxx Incoming Call"
  const bodyText = document.body.innerText || '';

  // Find text around "Incoming Call"
  const match = bodyText.match(/(.{0,100})Incoming [Cc]all/);
  if (match) {
    const context = match[1].trim();
    // Extract phone number
    const phoneMatch = context.match(/(\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4})/);
    const phone = phoneMatch ? phoneMatch[1] : '';
    // Extract name (everything before "Google Voice" or the phone number)
    const nameMatch = context.match(/^(.+?)(?:Google Voice|\(\d{3}\))/);
    const name = nameMatch ? nameMatch[1].trim() : '';
    return name ? `${name} ${phone}`.trim() : phone || context.substring(0, 50);
  }

  // Fallback: look for any phone number on the page
  const phoneMatch = bodyText.match(/(\(?\d{3}\)?[\s\-.]?\d{3}[\s\-.]?\d{4})/);
  return phoneMatch ? phoneMatch[1] : 'Unknown';
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

// --- Extension message listener (receives audioFrames relayed from offscreen via background) ---

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === 'audioFrame' && msg.direction === 'capture') {
    // Captured tab audio → forward to bridge server via WebSocket
    sendToServer({ type: 'audioFrame', pcm: msg.pcm });
  }
  // Return false for synchronous handling
  return false;
});

// --- Call Detection via Polling ---
// GV renders incoming call UI in the main content area (replaces dial pad).
// The panel shows caller info + "Incoming Call" text + red/green accept/reject buttons.
// We poll every 500ms — more reliable than MutationObserver for text detection.

let callPollInterval = null;

function startCallPolling() {
  if (callPollInterval) return;
  callPollInterval = setInterval(() => {
    try {
      // Detect active call UI by checking for specific buttons.
      // DO NOT use document.body.innerText for "Incoming call" — the call history
      // list also contains that text, causing false positives.
      let hasAnswerBtn = false;
      let hasDeclineBtn = false;
      let hasEndCallBtn = false;
      let hasMuteBtn = false;
      let hasHoldBtn = false;
      document.querySelectorAll('button').forEach(btn => {
        const label = (btn.getAttribute('aria-label') || '').toLowerCase();
        const text = (btn.innerText || '').toLowerCase();
        const combined = label + ' ' + text;
        if (/\banswer\b|\baccept\b/.test(combined)) hasAnswerBtn = true;
        if (/\bdecline\b|\breject\b/.test(combined)) hasDeclineBtn = true;
        if (/\bend call\b|\bhang up\b/.test(combined)) hasEndCallBtn = true;
        if (/\bmute\b/.test(combined)) hasMuteBtn = true;
        if (/\bhold\b/.test(combined)) hasHoldBtn = true;
      });

      // Active incoming call = answer + decline buttons visible (from the screenshot)
      // Also check for Hold/Mute/Keypad which appear in the call panel
      const hasActiveCallPanel = (hasAnswerBtn || hasDeclineBtn) || (hasMuteBtn && hasHoldBtn);
      const hasIncomingCall = hasAnswerBtn || hasDeclineBtn;

      // Debug: write poll state to DOM attribute
      document.documentElement.setAttribute('data-gvbridge-poll',
        Date.now() + '|ws=' + (ws ? ws.readyState : 'null') + '|answer=' + hasAnswerBtn + '|decline=' + hasDeclineBtn + '|endcall=' + hasEndCallBtn + '|mute=' + hasMuteBtn + '|detected=' + incomingDetected + '|active=' + callActive);

      // Incoming call: answer/decline buttons visible
      if (hasIncomingCall && !incomingDetected && !callActive) {
        incomingDetected = true;
        const callerInfo = extractCallerInfo();
        console.log('[GVBridge] INCOMING CALL DETECTED:', callerInfo);
        // Send via BOTH WebSocket (if connected) and HTTP POST (reliable fallback)
        const msg = { type: 'incomingCall', from: callerInfo || 'Unknown', callId: `gv-${Date.now()}` };
        sendToServer(msg);
        sendViaHttp(msg);
      }

      // Call answered: mute/end-call buttons visible but answer button GONE
      // (GV shows answer+endcall+mute together during ringing; when user clicks
      // answer, the answer button disappears but endcall+mute remain)
      if (hasMuteBtn && !hasAnswerBtn && !hasDeclineBtn && incomingDetected && !callActive) {
        callActive = true;
        incomingDetected = false;
        console.log('[GVBridge] CALL ANSWERED');
        const msg = { type: 'callAnswered', callId: `gv-${Date.now()}` };
        sendToServer(msg);
        sendViaHttp(msg);
        startAudioCapture();
      }

      // Call ended: was in call but no more end-call/mute buttons
      if (callActive && !hasEndCallBtn && !hasMuteBtn) {
        callActive = false;
        console.log('[GVBridge] CALL ENDED');
        const msg = { type: 'callEnded', callId: `gv-${Date.now()}` };
        sendToServer(msg);
        sendViaHttp(msg);
        stopAudioCapture();
      }

      // Incoming call missed/declined: was ringing but buttons gone
      if (incomingDetected && !callActive && !hasAnswerBtn && !hasDeclineBtn) {
        incomingDetected = false;
        console.log('[GVBridge] INCOMING CALL ENDED (missed/declined)');
        const msg = { type: 'callEnded', callId: `gv-${Date.now()}` };
        sendToServer(msg);
        sendViaHttp(msg);
      }
    } catch (e) {
      // Don't let polling errors crash the script
    }
  }, 500);
  console.log('[GVBridge] Call polling started (500ms interval)');
}

// --- Init ---
setupObserver();
connect();
startCallPolling();
console.log('[GVBridge] Content script loaded on', window.location.href);
