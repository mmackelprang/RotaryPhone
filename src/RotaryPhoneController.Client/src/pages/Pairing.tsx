import { useEffect, useState, useCallback } from 'react';
import {
  Box, Card, CardContent, Typography, Button, List, ListItem, ListItemText,
  Chip, Stack, Dialog, DialogTitle, DialogContent, DialogActions, IconButton,
  CircularProgress
} from '@mui/material';
import BluetoothIcon from '@mui/icons-material/Bluetooth';
import BluetoothConnectedIcon from '@mui/icons-material/BluetoothConnected';
import BluetoothSearchingIcon from '@mui/icons-material/BluetoothSearching';
import DeleteIcon from '@mui/icons-material/Delete';
import LinkIcon from '@mui/icons-material/Link';
import LinkOffIcon from '@mui/icons-material/LinkOff';
import api from '../services/api';
import { signalRService } from '../services/SignalRService';

interface BluetoothDevice {
  address: string;
  name: string | null;
  isConnected: boolean;
  isPaired: boolean;
  hasActiveCall: boolean;
  hasIncomingCall: boolean;
  hasScoAudio: boolean;
}

interface PairingRequestData {
  address: string;
  type: string;
  passkey: string | null;
}

interface DevicesResponse {
  paired: BluetoothDevice[];
  connected: BluetoothDevice[];
  adapterReady: boolean;
  adapterAddress: string | null;
}

export default function Pairing() {
  const [paired, setPaired] = useState<BluetoothDevice[]>([]);
  const [connected, setConnected] = useState<BluetoothDevice[]>([]);
  const [discovered, setDiscovered] = useState<BluetoothDevice[]>([]);
  const [adapterReady, setAdapterReady] = useState(false);
  const [adapterAddress, setAdapterAddress] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [pairingRequest, setPairingRequest] = useState<PairingRequestData | null>(null);

  const fetchDevices = useCallback(async () => {
    try {
      const res = await api.get<DevicesResponse>('/bluetooth/devices');
      setPaired(res.data.paired);
      setConnected(res.data.connected);
      setAdapterReady(res.data.adapterReady);
      setAdapterAddress(res.data.adapterAddress);
    } catch (err) {
      console.error('Failed to fetch devices:', err);
    }
  }, []);

  useEffect(() => {
    fetchDevices();

    const unsubs = [
      signalRService.on('DeviceConnected', (_addr: string, _name: string) => fetchDevices()),
      signalRService.on('DeviceDisconnected', (_addr: string) => fetchDevices()),
      signalRService.on('DevicePaired', (_addr: string, _name: string) => fetchDevices()),
      signalRService.on('DeviceRemoved', (_addr: string) => {
        fetchDevices();
        setDiscovered(prev => prev.filter(d => d.address !== _addr));
      }),
      signalRService.on('DeviceDiscovered', (address: string, name: string) => {
        setDiscovered(prev => {
          if (prev.some(d => d.address === address)) return prev;
          return [...prev, { address, name, isConnected: false, isPaired: false, hasActiveCall: false, hasIncomingCall: false, hasScoAudio: false }];
        });
      }),
      signalRService.on('PairingRequest', (address: string, type: string, passkey: string | null) => {
        setPairingRequest({ address, type, passkey });
      }),
    ];

    return () => unsubs.forEach(unsub => unsub());
  }, [fetchDevices]);

  const startScan = async () => {
    setDiscovered([]);
    setScanning(true);
    try {
      await api.post('/bluetooth/discovery/start');
    } catch (err) {
      console.error('Failed to start discovery:', err);
      setScanning(false);
    }
  };

  const stopScan = async () => {
    setScanning(false);
    try {
      await api.post('/bluetooth/discovery/stop');
    } catch (err) {
      console.error('Failed to stop discovery:', err);
    }
  };

  const pairDevice = async (address: string) => {
    try {
      await api.post('/bluetooth/pair', { address });
    } catch (err) {
      console.error('Pair failed:', err);
    }
  };

  const removeDevice = async (address: string) => {
    try {
      await api.delete(`/bluetooth/devices/${address}`);
      fetchDevices();
    } catch (err) {
      console.error('Remove failed:', err);
    }
  };

  const connectDevice = async (address: string) => {
    try {
      await api.post(`/bluetooth/devices/${address}/connect`);
    } catch (err) {
      console.error('Connect failed:', err);
    }
  };

  const disconnectDevice = async (address: string) => {
    try {
      await api.post(`/bluetooth/devices/${address}/disconnect`);
    } catch (err) {
      console.error('Disconnect failed:', err);
    }
  };

  const confirmPairing = async (accept: boolean) => {
    if (!pairingRequest) return;
    try {
      await api.post('/bluetooth/pairing/confirm', { address: pairingRequest.address, accept });
    } catch (err) {
      console.error('Confirm pairing failed:', err);
    }
    setPairingRequest(null);
  };

  const isDeviceConnected = (address: string) =>
    connected.some(d => d.address === address);

  return (
    <Box>
      <Stack direction="row" alignItems="center" spacing={1} sx={{ mb: 3 }}>
        <BluetoothIcon color="primary" />
        <Typography variant="h5">Bluetooth Devices</Typography>
        <Chip
          label={adapterReady ? 'Adapter Ready' : 'Adapter Offline'}
          color={adapterReady ? 'success' : 'error'}
          size="small"
        />
        {adapterAddress && (
          <Typography variant="body2" color="text.secondary">{adapterAddress}</Typography>
        )}
      </Stack>

      {/* Paired Devices */}
      <Card sx={{ mb: 3 }}>
        <CardContent>
          <Typography variant="h6" gutterBottom>Paired Devices</Typography>
          {paired.length === 0 ? (
            <Typography variant="body2" color="text.secondary">No paired devices</Typography>
          ) : (
            <List disablePadding>
              {paired.map(device => (
                <ListItem
                  key={device.address}
                  secondaryAction={
                    <Stack direction="row" spacing={1}>
                      {isDeviceConnected(device.address) ? (
                        <IconButton onClick={() => disconnectDevice(device.address)} title="Disconnect">
                          <LinkOffIcon />
                        </IconButton>
                      ) : (
                        <IconButton onClick={() => connectDevice(device.address)} title="Connect" color="primary">
                          <LinkIcon />
                        </IconButton>
                      )}
                      <IconButton onClick={() => removeDevice(device.address)} title="Remove" color="error">
                        <DeleteIcon />
                      </IconButton>
                    </Stack>
                  }
                >
                  <ListItemText
                    primary={device.name || device.address}
                    secondary={device.address}
                  />
                  {isDeviceConnected(device.address) ? (
                    <Chip icon={<BluetoothConnectedIcon />} label="Connected" color="success" size="small" sx={{ mr: 2 }} />
                  ) : (
                    <Chip label="Paired" color="default" size="small" sx={{ mr: 2 }} />
                  )}
                  {device.hasActiveCall && <Chip label="In Call" color="warning" size="small" sx={{ mr: 1 }} />}
                </ListItem>
              ))}
            </List>
          )}
        </CardContent>
      </Card>

      {/* Discovery */}
      <Card sx={{ mb: 3 }}>
        <CardContent>
          <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 2 }}>
            <Typography variant="h6">
              {scanning ? 'Scanning...' : 'Discover Devices'}
            </Typography>
            <Button
              variant="contained"
              startIcon={scanning ? <CircularProgress size={16} color="inherit" /> : <BluetoothSearchingIcon />}
              onClick={scanning ? stopScan : startScan}
              color={scanning ? 'error' : 'primary'}
            >
              {scanning ? 'Stop Scan' : 'Start Scan'}
            </Button>
          </Stack>
          {discovered.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              {scanning ? 'Searching for nearby devices...' : 'Press Start Scan to find devices'}
            </Typography>
          ) : (
            <List disablePadding>
              {discovered.map(device => (
                <ListItem
                  key={device.address}
                  secondaryAction={
                    <Button
                      variant="outlined"
                      size="small"
                      onClick={() => pairDevice(device.address)}
                    >
                      Pair
                    </Button>
                  }
                >
                  <ListItemText
                    primary={device.name || 'Unknown Device'}
                    secondary={device.address}
                  />
                </ListItem>
              ))}
            </List>
          )}
        </CardContent>
      </Card>

      {/* Pairing Confirmation Dialog */}
      <Dialog open={pairingRequest !== null} onClose={() => confirmPairing(false)}>
        <DialogTitle>Pairing Request</DialogTitle>
        <DialogContent>
          <Typography>
            Device <strong>{pairingRequest?.address}</strong> wants to pair.
          </Typography>
          {pairingRequest?.passkey && (
            <Typography variant="h4" align="center" sx={{ my: 2, fontFamily: 'monospace' }}>
              {pairingRequest.passkey}
            </Typography>
          )}
          <Typography variant="body2" color="text.secondary">
            {pairingRequest?.type === 'confirmation'
              ? 'Confirm the passkey matches on both devices.'
              : 'Enter the PIN on your phone.'}
          </Typography>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => confirmPairing(false)} color="error">Reject</Button>
          <Button onClick={() => confirmPairing(true)} variant="contained">Accept</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
