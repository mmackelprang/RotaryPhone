import { useState, useEffect, useCallback } from 'react';
import { HubConnectionBuilder } from '@microsoft/signalr';

interface BridgeStatus {
  extensionConnected: boolean;
  extensionVersion: string | null;
  activeMode: string;
}

interface AdapterMode {
  mode: string;
}

interface SmsNotification {
  fromNumber: string;
  body: string | null;
  receivedAt: string;
  type: 'Sms' | 'MissedCall';
}

export function useGVBridge() {
  const [status, setStatus] = useState<BridgeStatus>({
    extensionConnected: false, extensionVersion: null, activeMode: 'BluetoothHfp'
  });
  const [smsMessages, setSmsMessages] = useState<SmsNotification[]>([]);
  const [modes, setModes] = useState<AdapterMode[]>([]);

  useEffect(() => {
    fetch('/api/gvbridge/status').then(r => r.json()).then(setStatus).catch(() => {});
    fetch('/api/gvbridge/sms').then(r => r.json()).then(setSmsMessages).catch(() => {});
    fetch('/api/gvbridge/adapter/mode').then(r => r.json()).then(d => {
      setModes(d.modes || []);
      setStatus(prev => ({ ...prev, activeMode: d.activeMode }));
    }).catch(() => {});

    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/gvbridge')
      .withAutomaticReconnect()
      .build();

    hub.on('ExtensionConnectionChanged', (data: { connected: boolean }) => {
      setStatus(prev => ({ ...prev, extensionConnected: data.connected }));
    });

    hub.on('ModeChanged', (data: { activeMode: string }) => {
      setStatus(prev => ({ ...prev, activeMode: data.activeMode }));
    });

    hub.start().catch(err => console.error('GVBridge hub error:', err));
    return () => { hub.stop(); };
  }, []);

  const switchMode = useCallback(async (mode: string) => {
    await fetch('/api/gvbridge/adapter/mode', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ mode })
    });
  }, []);

  const sendSms = useCallback(async (to: string, body: string) => {
    await fetch('/api/gvbridge/sms/send', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ to, body })
    });
  }, []);

  return { status, smsMessages, modes, switchMode, sendSms };
}
