import { useEffect, useState } from 'react';
import { 
  Box, Typography, List, ListItem, ListItemText, 
  ListItemIcon, Paper, Chip, Stack, Button 
} from '@mui/material';
import { CallMade, CallReceived, DeleteSweep } from '@mui/icons-material';
import api from '../services/api';
import type { CallHistoryEntry } from '../types';

const CallHistory: React.FC = () => {
  const [history, setHistory] = useState<CallHistoryEntry[]>([]);
  const [loading, setLoading] = useState(false);

  const fetchHistory = async () => {
    setLoading(true);
    try {
      const response = await api.get<CallHistoryEntry[]>('/callhistory');
      setHistory(response.data);
    } catch (error) {
      console.error('Failed to fetch history', error);
    } finally {
      setLoading(false);
    }
  };

  const handleClear = async () => {
    if (!confirm('Clear all call history?')) return;
    try {
      await api.delete('/callhistory');
      fetchHistory();
    } catch (error) {
      console.error('Failed to clear history', error);
    }
  };

  useEffect(() => {
    fetchHistory();
    
    // Optional: Poll or listen to SignalR updates
    const interval = setInterval(fetchHistory, 5000); 
    return () => clearInterval(interval);
  }, []);

  return (
    <Box>
      <Stack direction="row" justifyContent="space-between" alignItems="center" mb={2}>
        <Typography variant="h5">Call History</Typography>
        <Button 
          variant="outlined" 
          color="error" 
          startIcon={<DeleteSweep />}
          onClick={handleClear}
        >
          Clear History
        </Button>
      </Stack>

      <Paper variant="outlined" sx={{ borderRadius: 0 }}>
        <List>
          {history.length === 0 && !loading && (
            <ListItem>
              <ListItemText primary="No calls recorded." />
            </ListItem>
          )}
          
          {history.map((entry, index) => (
            <ListItem key={entry.id || index} divider>
              <ListItemIcon>
                {entry.direction === 'Incoming' ? (
                  <CallReceived color={entry.duration ? 'success' : 'error'} />
                ) : (
                  <CallMade color="primary" />
                )}
              </ListItemIcon>
              <ListItemText 
                primary={entry.phoneNumber}
                secondary={new Date(entry.startTime).toLocaleString()}
                primaryTypographyProps={{ variant: 'h6', fontFamily: 'monospace' }}
              />
              <Stack direction="row" spacing={1} alignItems="center">
                {entry.answeredOn && (
                  <Chip 
                    label={entry.answeredOn === 'RotaryPhone' ? 'ROTARY' : 'MOBILE'} 
                    size="small" 
                    color="default" 
                    variant="outlined"
                  />
                )}
                {entry.duration && (
                  <Typography variant="body2" sx={{ fontFamily: 'monospace' }}>
                    {entry.duration}
                  </Typography>
                )}
              </Stack>
            </ListItem>
          ))}
        </List>
      </Paper>
    </Box>
  );
};

export default CallHistory;
