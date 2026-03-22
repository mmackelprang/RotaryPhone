// RotaryPhone GV Bridge — Offscreen Audio Bridge
// Captures tab audio via tabCapture streamId, downsamples to 16kHz mono Int16 PCM,
// chunks into 20ms frames (640 bytes), base64-encodes, and relays to background/content.

'use strict';

console.log('[GVBridge Audio] Offscreen document loaded');

let audioCtx = null;
let mediaStream = null;
let scriptNode = null;
let sourceNode = null;

// --- Downsampling helpers ---

/**
 * Downsample a Float32Array from inputRate to outputRate using linear interpolation.
 * Returns a new Float32Array at the target rate.
 */
function downsample(buffer, inputRate, outputRate) {
  if (inputRate === outputRate) return buffer;
  const ratio = inputRate / outputRate;
  const newLength = Math.floor(buffer.length / ratio);
  const result = new Float32Array(newLength);
  for (let i = 0; i < newLength; i++) {
    const srcIndex = i * ratio;
    const low = Math.floor(srcIndex);
    const high = Math.min(low + 1, buffer.length - 1);
    const frac = srcIndex - low;
    result[i] = buffer[low] * (1 - frac) + buffer[high] * frac;
  }
  return result;
}

/**
 * Convert Float32 samples (-1..1) to Int16 PCM.
 * Returns a Uint8Array (little-endian Int16 bytes).
 */
function float32ToInt16(float32Array) {
  const int16 = new Int16Array(float32Array.length);
  for (let i = 0; i < float32Array.length; i++) {
    const s = Math.max(-1, Math.min(1, float32Array[i]));
    int16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
  }
  return new Uint8Array(int16.buffer);
}

/**
 * Encode a Uint8Array to base64.
 */
function uint8ToBase64(uint8Array) {
  let binary = '';
  for (let i = 0; i < uint8Array.length; i++) {
    binary += String.fromCharCode(uint8Array[i]);
  }
  return btoa(binary);
}

// --- PCM frame accumulator ---
// 16kHz mono Int16 = 2 bytes/sample. 20ms = 320 samples = 640 bytes per frame.
const TARGET_RATE = 16000;
const FRAME_SAMPLES = 320; // 20ms at 16kHz
const FRAME_BYTES = FRAME_SAMPLES * 2; // 640 bytes
let pcmAccumulator = new Uint8Array(0);

function flushFrames() {
  while (pcmAccumulator.length >= FRAME_BYTES) {
    const frame = pcmAccumulator.slice(0, FRAME_BYTES);
    pcmAccumulator = pcmAccumulator.slice(FRAME_BYTES);
    const b64 = uint8ToBase64(frame);
    chrome.runtime.sendMessage({ type: 'audioFrame', pcm: b64 });
  }
}

// --- Capture lifecycle ---

async function startCapture(streamId) {
  if (audioCtx) {
    console.warn('[GVBridge Audio] Capture already active, stopping first');
    stopCapture();
  }

  try {
    // Use the streamId obtained from tabCapture.getMediaStreamId
    mediaStream = await navigator.mediaDevices.getUserMedia({
      audio: {
        mandatory: {
          chromeMediaSource: 'tab',
          chromeMediaSourceId: streamId,
        },
      },
      video: false,
    });

    audioCtx = new AudioContext();
    const inputRate = audioCtx.sampleRate;
    console.log(`[GVBridge Audio] Capture started — input sample rate: ${inputRate}Hz`);

    sourceNode = audioCtx.createMediaStreamSource(mediaStream);

    // ScriptProcessorNode with 4096 buffer, 1 input channel, 1 output channel.
    // Deprecated but reliable in Chromium offscreen docs; AudioWorklets add complexity.
    scriptNode = audioCtx.createScriptProcessor(4096, 1, 1);

    scriptNode.onaudioprocess = (event) => {
      const input = event.inputBuffer.getChannelData(0); // mono channel 0
      const downsampled = downsample(input, inputRate, TARGET_RATE);
      const pcmBytes = float32ToInt16(downsampled);

      // Append to accumulator and flush complete 20ms frames
      const merged = new Uint8Array(pcmAccumulator.length + pcmBytes.length);
      merged.set(pcmAccumulator, 0);
      merged.set(pcmBytes, pcmAccumulator.length);
      pcmAccumulator = merged;
      flushFrames();
    };

    sourceNode.connect(scriptNode);
    // Connect to destination to keep the processor running (silent output)
    scriptNode.connect(audioCtx.destination);

    console.log('[GVBridge Audio] Audio pipeline connected');
  } catch (err) {
    console.error('[GVBridge Audio] Failed to start capture:', err);
    stopCapture();
  }
}

function stopCapture() {
  console.log('[GVBridge Audio] Stopping capture');

  if (scriptNode) {
    scriptNode.disconnect();
    scriptNode.onaudioprocess = null;
    scriptNode = null;
  }
  if (sourceNode) {
    sourceNode.disconnect();
    sourceNode = null;
  }
  if (mediaStream) {
    mediaStream.getTracks().forEach((t) => t.stop());
    mediaStream = null;
  }
  if (audioCtx) {
    audioCtx.close().catch(() => {});
    audioCtx = null;
  }
  pcmAccumulator = new Uint8Array(0);
}

// --- Message handler ---

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  switch (msg.type) {
    case 'startCapture':
      console.log('[GVBridge Audio] startCapture received, streamId:', msg.streamId);
      startCapture(msg.streamId)
        .then(() => sendResponse({ ok: true }))
        .catch((err) => sendResponse({ ok: false, error: err.message }));
      return true; // async sendResponse

    case 'stopCapture':
      stopCapture();
      sendResponse({ ok: true });
      return false;

    case 'audioFrame':
      // TODO Phase 2: Playback direction — inject received PCM into a MediaStream
      // that replaces the GV caller's microphone input via getUserMedia override.
      // For now, just acknowledge receipt.
      console.log('[GVBridge Audio] Received audioFrame for playback (not yet implemented)');
      sendResponse({ ok: true });
      return false;

    default:
      return false;
  }
});
