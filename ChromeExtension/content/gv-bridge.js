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
      console.log('[GVBridge] Received ANSWER command from server');
      answer();
      break;
    case 'hangup':
      hangup();
      break;
    case 'sendSms':
      sendSms(msg.to, msg.body);
      break;
    case 'muteTab':
      document.querySelectorAll('audio,video').forEach(e => e.muted = true);
      try {
        const contexts = window.__audioContexts || [];
        contexts.forEach(ctx => { if (ctx.state === 'running') ctx.suspend(); });
      } catch(e) {}
      console.log('[GV Bridge] Tab muted');
      break;
    case 'unmuteTab':
      document.querySelectorAll('audio,video').forEach(e => e.muted = false);
      console.log('[GV Bridge] Tab unmuted');
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
    // Try the ARIA selector first
    let btn = document.querySelector(SELECTORS.answerButton);
    if (!btn) {
      // Fallback: find any button with "answer" or "accept" in its label/text
      document.querySelectorAll('button').forEach(b => {
        const label = (b.getAttribute('aria-label') || b.innerText || '').toLowerCase();
        if (/\banswer\b|\baccept\b/.test(label)) btn = b;
      });
    }
    if (!btn) {
      console.log('[GVBridge] Answer button not found');
      return false;
    }
    console.log('[GVBridge] Clicking answer button:', btn.getAttribute('aria-label') || btn.innerText);
    btn.click();
    return true;
  });
}

function hangup() {
  retryAction(() => {
    const btn = document.querySelector(SELECTORS.hangupButton);
    if (!btn) return false;
    btn.click();
    return true;
  });
}

function sendSms(to, body) {
  console.log('[GVBridge] sendSms not fully implemented — requires thread navigation');
}

// --- Extension message listener (receives audioFrames relayed from offscreen via background) ---

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === 'audioFrame' && msg.direction === 'capture') {
    // Captured tab audio → forward to bridge server via WebSocket
    sendToServer({ type: 'audioFrame', pcm: msg.pcm });
  }
  // Return false for synchronous handling
  return false;
});

// --- Init ---
setupObserver();
connect();
console.log('[GVBridge] Content script loaded on', window.location.href);
