import * as signalR from '@microsoft/signalr';
import { useStore, CallState } from '../store/useStore';
import type { SystemStatus } from '../store/useStore';

const HUB_URL = 'http://localhost:5555/hub';

class SignalRService {
  private connection: signalR.HubConnection;

  constructor() {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URL, {
        withCredentials: true // Important for CORS
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Information)
      .build();

    this.connection.on('CallStateChanged', (phoneId: string, state: string) => {
      console.log(`SignalR: State changed for ${phoneId} to ${state}`);
      // Map string to enum
      const callState = state as CallState;
      useStore.getState().setCallState(callState);
    });

    this.connection.on('IncomingCall', (phoneId: string, phoneNumber: string) => {
      console.log(`SignalR: Incoming call for ${phoneId} from ${phoneNumber}`);
      useStore.getState().setIncomingNumber(phoneNumber);
    });

    this.connection.on('SystemStatusChanged', (status: SystemStatus) => {
      console.log(`SignalR: System status changed - Platform: ${status.platform}, Bluetooth: ${status.bluetoothConnected}, SIP: ${status.sipListening}`);
      useStore.getState().setSystemStatus(status);
    });
  }

  public async start() {
    try {
      await this.connection.start();
      console.log('SignalR Connected.');
    } catch (err) {
      console.error('SignalR Connection Error: ', err);
      setTimeout(() => this.start(), 5000);
    }
  }

  public stop() {
    this.connection.stop();
  }
}

export const signalRService = new SignalRService();
