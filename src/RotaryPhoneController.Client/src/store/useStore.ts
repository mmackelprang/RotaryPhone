import { create } from 'zustand';

export const CallState = {
  Idle: 'Idle',
  Dialing: 'Dialing',
  Ringing: 'Ringing',
  InCall: 'InCall'
} as const;

export type CallState = typeof CallState[keyof typeof CallState];

interface PhoneState {
  callState: CallState;
  dialedNumber: string;
  incomingNumber: string | null;
  
  setCallState: (state: CallState) => void;
  setDialedNumber: (number: string) => void;
  setIncomingNumber: (number: string | null) => void;
}

export const useStore = create<PhoneState>((set) => ({
  callState: CallState.Idle,
  dialedNumber: '',
  incomingNumber: null,

  setCallState: (state) => set({ callState: state }),
  setDialedNumber: (number) => set({ dialedNumber: number }),
  setIncomingNumber: (number) => set({ incomingNumber: number }),
}));
