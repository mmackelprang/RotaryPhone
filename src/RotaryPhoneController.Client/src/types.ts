export interface Contact {
  id: string;
  name: string;
  phoneNumber: string;
  email?: string;
  notes?: string;
  createdAt: string;
  modifiedAt: string;
}

export interface CallHistoryEntry {
  id?: string;
  startTime: string;
  endTime?: string;
  duration?: string;
  direction: 'Incoming' | 'Outgoing';
  phoneNumber: string;
  answeredOn?: 'RotaryPhone' | 'CellPhone';
  phoneId: string;
}
