import { useState, useEffect, useCallback } from 'react';
import { HubConnectionBuilder } from '@microsoft/signalr';

interface TrunkStatus {
  isRegistered: boolean;
  callState: string;
  activeCallDurationSeconds: number;
}

interface CallLogEntry {
  id: number;
  startedAt: string;
  endedAt: string | null;
  direction: string;
  remoteNumber: string;
  status: string;
  durationSeconds: number | null;
}

interface SmsNotification {
  fromNumber: string;
  body: string | null;
  receivedAt: string;
  type: 'Sms' | 'MissedCall';
}

export function useGVTrunk() {
  const [status, setStatus] = useState<TrunkStatus>({ isRegistered: false, callState: 'Unknown', activeCallDurationSeconds: 0 });
  const [calls, setCalls] = useState<CallLogEntry[]>([]);
  const [smsMessages, setSmsMessages] = useState<SmsNotification[]>([]);

  useEffect(() => {
    fetch('/api/gvtrunk/status').then(r => r.json()).then(setStatus).catch(() => {});
    fetch('/api/gvtrunk/calls').then(r => r.json()).then(setCalls).catch(() => {});
    fetch('/api/gvtrunk/sms').then(r => r.json()).then(setSmsMessages).catch(() => {});

    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/gvtrunk')
      .withAutomaticReconnect()
      .build();

    hub.on('RegistrationChanged', (data: { isRegistered: boolean }) => {
      setStatus(prev => ({ ...prev, ...data }));
    });

    hub.on('SmsReceived', (notification: SmsNotification) => {
      setSmsMessages(prev => [...prev.slice(-19), notification]);
    });

    hub.on('MissedCallReceived', (notification: SmsNotification) => {
      setSmsMessages(prev => [...prev.slice(-19), notification]);
    });

    hub.on('CallStateChanged', (data: { phoneId: string; callState: string }) => {
      setStatus(prev => ({ ...prev, callState: data.callState }));
    });

    hub.start().catch(err => console.error('GVTrunk hub error:', err));

    return () => { hub.stop(); };
  }, []);

  const dial = useCallback(async (number: string) => {
    await fetch('/api/gvtrunk/dial', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ number })
    });
  }, []);

  const forceReregister = useCallback(async () => {
    await fetch('/api/gvtrunk/reregister', { method: 'POST' });
  }, []);

  const refreshCalls = useCallback(async () => {
    const r = await fetch('/api/gvtrunk/calls');
    setCalls(await r.json());
  }, []);

  return { status, calls, smsMessages, dial, forceReregister, refreshCalls };
}
