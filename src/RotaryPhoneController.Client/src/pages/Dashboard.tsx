import { useState } from 'react';
import { 
  Grid, Card, CardContent, Typography, Button, TextField, 
  Box, Chip, Stack, Divider 
} from '@mui/material';
import { Phone, RingVolume, CallEnd, Dialpad } from '@mui/icons-material';
import { useStore, CallState } from '../store/useStore';
import api from '../services/api';

const Dashboard: React.FC = () => {
  const { callState, dialedNumber, incomingNumber } = useStore();
  const [dialInput, setDialInput] = useState('');

  // Status Color Logic
  const getStatusColor = (state: CallState) => {
    switch (state) {
      case CallState.Idle: return 'success';
      case CallState.Ringing: return 'warning';
      case CallState.InCall: return 'error'; // Red for busy/active
      case CallState.Dialing: return 'info';
      default: return 'default';
    }
  };

  // Mock Actions
  const simulateIncoming = async () => {
    await api.post('/phone/simulate/incoming');
  };

  const simulateLiftHandset = async () => {
    await api.post('/phone/simulate/hook?offHook=true');
  };

  const simulateDropHandset = async () => {
    await api.post('/phone/simulate/hook?offHook=false');
  };

  const simulateDial = async () => {
    if (!dialInput) return;
    await api.post(`/phone/simulate/dial?digits=${dialInput}`);
    setDialInput('');
  };

  return (
    <Grid container spacing={3}>
      {/* Phone Status Card */}
      <Grid size={{ xs: 12, md: 6 }}>
        <Card sx={{ height: '100%', display: 'flex', flexDirection: 'column', justifyContent: 'center' }}>
          <CardContent sx={{ textAlign: 'center' }}>
            <Typography variant="h6" color="text.secondary" gutterBottom>
              CURRENT STATUS
            </Typography>
            
            <Box sx={{ my: 4 }}>
              <Chip 
                label={callState.toUpperCase()} 
                color={getStatusColor(callState)}
                sx={{ 
                  fontSize: '3rem', 
                  height: 'auto', 
                  py: 1, px: 4, 
                  borderRadius: 2 
                }} 
              />
            </Box>

            {callState === CallState.Ringing && (
              <Typography variant="h5" color="warning.main" sx={{ mt: 2 }}>
                Incoming Call: {incomingNumber || 'Unknown'}
              </Typography>
            )}

            {(callState === CallState.Dialing || callState === CallState.InCall) && (
              <Typography variant="h4" sx={{ mt: 2, fontFamily: 'monospace' }}>
                {dialedNumber || '...'}
              </Typography>
            )}
          </CardContent>
        </Card>
      </Grid>

      {/* Control Panel (Mock) */}
      <Grid size={{ xs: 12, md: 6 }}>
        <Card>
          <CardContent>
            <Typography variant="h6" color="primary" gutterBottom>
              DEV CONTROLS
            </Typography>
            <Typography variant="body2" color="text.secondary" paragraph>
              Simulate hardware events when no rotary phone is connected.
            </Typography>

            <Stack spacing={2}>
              <Box>
                <Typography variant="subtitle2" gutterBottom>HANDSET</Typography>
                <Stack direction="row" spacing={2}>
                  <Button 
                    variant="contained" 
                    color="success" 
                    startIcon={<Phone />}
                    onClick={simulateLiftHandset}
                    disabled={callState !== CallState.Idle && callState !== CallState.Ringing}
                  >
                    LIFT HANDSET
                  </Button>
                  <Button 
                    variant="contained" 
                    color="error" 
                    startIcon={<CallEnd />}
                    onClick={simulateDropHandset}
                    disabled={callState === CallState.Idle}
                  >
                    DROP HANDSET
                  </Button>
                </Stack>
              </Box>

              <Divider />

              <Box>
                <Typography variant="subtitle2" gutterBottom>NETWORK</Typography>
                <Button 
                  variant="outlined" 
                  color="warning" 
                  startIcon={<RingVolume />}
                  onClick={simulateIncoming}
                  fullWidth
                  disabled={callState !== CallState.Idle}
                >
                  SIMULATE INCOMING CALL
                </Button>
              </Box>

              <Divider />

              <Box>
                <Typography variant="subtitle2" gutterBottom>DIALER</Typography>
                <Stack direction="row" spacing={1}>
                  <TextField 
                    label="Digits" 
                    variant="outlined" 
                    size="small" 
                    fullWidth
                    value={dialInput}
                    onChange={(e) => setDialInput(e.target.value)}
                    onKeyPress={(e) => e.key === 'Enter' && simulateDial()}
                  />
                  <Button 
                    variant="contained" 
                    startIcon={<Dialpad />}
                    onClick={simulateDial}
                    disabled={callState !== CallState.Dialing}
                  >
                    DIAL
                  </Button>
                </Stack>
              </Box>
            </Stack>
          </CardContent>
        </Card>
      </Grid>
    </Grid>
  );
};

export default Dashboard;
