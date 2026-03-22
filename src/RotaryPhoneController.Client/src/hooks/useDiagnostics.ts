import { useState, useEffect, useCallback, useRef } from 'react';
import { signalRService } from '../services/SignalRService.ts';
import { getDiagnosticsStatus } from '../services/api.ts';

export interface SipMessageEntry {
  id: string;
  timestamp: string;
  direction: 'Sent' | 'Received';
  method: string;
  fromAddress: string;
  toAddress: string;
  statusCode: number | null;
  statusReason: string | null;
  callId: string | null;
  summary: string | null;
  isFailed: boolean;
}

export interface CallTimelineEntry {
  id: string;
  timestamp: string;
  eventType: string;
  description: string;
  callId: string | null;
}

export interface Ht801HealthStatus {
  isRegistered: boolean;
  ipAddress: string | null;
  lastRegisterTime: string | null;
  registrationExpiry: number | null;
  pingMs: number | null;
  codec: string | null;
  hookState: string | null;
  firmware: string | null;
}

export interface DiagnosisAlert {
  id: string;
  timestamp: string;
  issue: string;
  suggestions: string[];
  relatedCallId: string | null;
}

export interface GvBridgeStatus {
  extensionConnected: boolean;
  extensionVersion: string | null;
  activeMode: string;
}

export interface RtpStats {
  isActive: boolean;
  packetsSent: number;
  packetsReceived: number;
  jitterMs: number;
  lossPercent: number;
  codec: string | null;
  localPort: number | null;
  remotePort: number | null;
}

export interface SipServerStatus {
  isListening: boolean;
  listenAddress: string | null;
  listenPort: number | null;
}

export interface DiagnosticsState {
  sipMessages: SipMessageEntry[];
  timeline: CallTimelineEntry[];
  ht801Health: Ht801HealthStatus;
  diagnoses: DiagnosisAlert[];
  gvBridgeStatus: GvBridgeStatus;
  rtpStats: RtpStats;
  sipServer: SipServerStatus;
  callState: string;
  loading: boolean;
}

const defaultHt801Health: Ht801HealthStatus = {
  isRegistered: false,
  ipAddress: null,
  lastRegisterTime: null,
  registrationExpiry: null,
  pingMs: null,
  codec: null,
  hookState: null,
  firmware: null,
};

const defaultGvBridgeStatus: GvBridgeStatus = {
  extensionConnected: false,
  extensionVersion: null,
  activeMode: 'BluetoothHfp',
};

const defaultRtpStats: RtpStats = {
  isActive: false,
  packetsSent: 0,
  packetsReceived: 0,
  jitterMs: 0,
  lossPercent: 0,
  codec: null,
  localPort: null,
  remotePort: null,
};

const defaultSipServer: SipServerStatus = {
  isListening: false,
  listenAddress: null,
  listenPort: null,
};

export function useDiagnostics() {
  const [sipMessages, setSipMessages] = useState<SipMessageEntry[]>([]);
  const [timeline, setTimeline] = useState<CallTimelineEntry[]>([]);
  const [ht801Health, setHt801Health] = useState<Ht801HealthStatus>(defaultHt801Health);
  const [diagnoses, setDiagnoses] = useState<DiagnosisAlert[]>([]);
  const [gvBridgeStatus, setGvBridgeStatus] = useState<GvBridgeStatus>(defaultGvBridgeStatus);
  const [rtpStats, setRtpStats] = useState<RtpStats>(defaultRtpStats);
  const [sipServer, setSipServer] = useState<SipServerStatus>(defaultSipServer);
  const [callState, setCallState] = useState<string>('Idle');
  const [loading, setLoading] = useState(true);
  const idCounter = useRef(0);

  const nextId = useCallback(() => {
    idCounter.current += 1;
    return `diag-${idCounter.current}`;
  }, []);

  // Load initial state
  useEffect(() => {
    getDiagnosticsStatus()
      .then((res) => {
        const data = res.data;
        // Server returns camelCase: recentSipMessages, recentTimeline, ht801, sip, gvBridge, gvAudioBridge
        if (data.recentSipMessages) setSipMessages(data.recentSipMessages);
        if (data.recentTimeline) setTimeline(data.recentTimeline);
        if (data.ht801) setHt801Health(data.ht801);
        if (data.gvBridge) setGvBridgeStatus(data.gvBridge);
        if (data.gvAudioBridge?.stats) setRtpStats(data.gvAudioBridge.stats);
        if (data.sip) setSipServer(data.sip);
      })
      .catch((err) => {
        console.error('Failed to load diagnostics status:', err);
      })
      .finally(() => setLoading(false));
  }, []);

  // Subscribe to SignalR events
  useEffect(() => {
    const unsubSip = signalRService.on('SipMessage', (msg: Omit<SipMessageEntry, 'id'>) => {
      setSipMessages((prev) => [...prev.slice(-199), { ...msg, id: nextId() }]);
    });

    const unsubTimeline = signalRService.on('CallTimeline', (entry: Omit<CallTimelineEntry, 'id'>) => {
      setTimeline((prev) => [...prev.slice(-199), { ...entry, id: nextId() }]);
    });

    const unsubHt801 = signalRService.on('Ht801Health', (health: Ht801HealthStatus) => {
      setHt801Health(health);
    });

    const unsubDiagnosis = signalRService.on('SipDiagnosis', (alert: Omit<DiagnosisAlert, 'id'>) => {
      setDiagnoses((prev) => [...prev.slice(-49), { ...alert, id: nextId() }]);
    });

    const unsubRtp = signalRService.on('RtpStats', (stats: RtpStats) => {
      setRtpStats(stats);
    });

    const unsubCallState = signalRService.on('CallStateChanged', (_phoneId: string, state: string) => {
      setCallState(state);
    });

    return () => {
      unsubSip();
      unsubTimeline();
      unsubHt801();
      unsubDiagnosis();
      unsubRtp();
      unsubCallState();
    };
  }, [nextId]);

  const clearDiagnoses = useCallback(() => {
    setDiagnoses([]);
  }, []);

  return {
    sipMessages,
    timeline,
    ht801Health,
    diagnoses,
    gvBridgeStatus,
    rtpStats,
    sipServer,
    callState,
    loading,
    clearDiagnoses,
  };
}
