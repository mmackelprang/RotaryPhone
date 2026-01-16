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
  systemStatus: SystemStatus | null;

  setCallState: (state: CallState) => void;
  setDialedNumber: (number: string) => void;
  setIncomingNumber: (number: string | null) => void;
  setSystemStatus: (status: SystemStatus) => void;
}

export const useStore = create<PhoneState>((set) => ({
  callState: CallState.Idle,
  dialedNumber: '',
  incomingNumber: null,
  systemStatus: null,

  setCallState: (state) => set({ callState: state }),
  setDialedNumber: (number) => set({ dialedNumber: number }),
  setIncomingNumber: (number) => set({ incomingNumber: number }),
  setSystemStatus: (status) => set({ systemStatus: status }),
}));
