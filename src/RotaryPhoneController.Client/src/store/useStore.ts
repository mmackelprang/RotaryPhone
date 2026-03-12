import { create } from 'zustand';

export const CallState = {
  Idle: 'Idle',
  Dialing: 'Dialing',
  Ringing: 'Ringing',
  InCall: 'InCall'
} as const;

export type CallState = typeof CallState[keyof typeof CallState];

export interface SystemStatus {
  platform: string;
  isRaspberryPi: boolean;
  bluetoothEnabled: boolean;
  bluetoothConnected: boolean;
  bluetoothDeviceAddress: string | null;
  sipListening: boolean;
  sipListenAddress: string;
  sipPort: number;
  ht801IpAddress: string | null;
  ht801Reachable: boolean | null;
}

interface PhoneState {
  callState: CallState;
  dialedNumber: string;
  incomingNumber: string | null;
  callerName: string | null;
  systemStatus: SystemStatus | null;

  setCallState: (state: CallState) => void;
  setDialedNumber: (number: string) => void;
  setIncomingNumber: (number: string | null) => void;
  setCallerName: (name: string | null) => void;
  setSystemStatus: (status: SystemStatus) => void;
}

export const useStore = create<PhoneState>((set) => ({
  callState: CallState.Idle,
  dialedNumber: '',
  incomingNumber: null,
  callerName: null,
  systemStatus: null,

  setCallState: (state) => set({
    callState: state,
    ...(state === CallState.Idle ? { callerName: null, incomingNumber: null } : {})
  }),
  setDialedNumber: (number) => set({ dialedNumber: number }),
  setIncomingNumber: (number) => set({ incomingNumber: number, callerName: null }),
  setCallerName: (name) => set({ callerName: name }),
  setSystemStatus: (status) => set({ systemStatus: status }),
}));
