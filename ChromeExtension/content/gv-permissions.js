// Runs in MAIN world at document_start, before Google Voice's scripts.
// Overrides Notification.permission to return 'granted' so GV will
// show incoming call UI and ring in the browser.
//
// Also intercepts new Notification() calls and dispatches a custom event
// so the isolated-world content script (gv-bridge.js) can detect them.

(function() {
  'use strict';

  const OriginalNotification = window.Notification;

  // Override Notification constructor to intercept incoming call notifications
  function PatchedNotification(title, options) {
    // Dispatch event for the content script to pick up
    window.dispatchEvent(new CustomEvent('gvbridge-notification', {
      detail: { title: title, body: options?.body || '', tag: options?.tag || '' }
    }));
    return new OriginalNotification(title, options);
  }

  // Copy static properties
  PatchedNotification.requestPermission = function() {
    return Promise.resolve('granted');
  };

  // Override permission to always return 'granted'
  Object.defineProperty(PatchedNotification, 'permission', {
    get: function() { return 'granted'; },
    configurable: true
  });

  // Preserve prototype chain
  PatchedNotification.prototype = OriginalNotification.prototype;

  // Replace global Notification
  window.Notification = PatchedNotification;

  // Also override navigator.permissions.query for notification permission
  // GV may check permissions this way instead of Notification.permission
  const origQuery = navigator.permissions.query.bind(navigator.permissions);
  navigator.permissions.query = function(desc) {
    if (desc && desc.name === 'notifications') {
      return Promise.resolve({ state: 'granted', onchange: null });
    }
    return origQuery(desc);
  };

  console.log('[GVBridge] Notification + permissions.query override installed');
})();
