import { useEffect, useRef } from 'react';
import { Box, Typography } from '@mui/material';
import type { CallTimelineEntry } from '../../hooks/useDiagnostics.ts';

interface CallTimelineProps {
  entries: CallTimelineEntry[];
}

const EVENT_COLORS: Record<string, string> = {
  InviteSent: '#4a9eff',
  InviteReceived: '#4a9eff',
  Ringing: '#f1c40f',
  Answered: '#2ecc71',
  Error: '#e74c3c',
  Ended: '#888',
  Bye: '#e74c3c',
  Audio: '#9b59b6',
  Register: '#e67e22',
};

function getEventColor(eventType: string): string {
  return EVENT_COLORS[eventType] ?? '#888';
}

function formatTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' });
  } catch {
    return iso;
  }
}

export default function CallTimeline({ entries }: CallTimelineProps) {
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = scrollRef.current;
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
  }, [entries.length]);

  return (
    <Box sx={{ bgcolor: '#111827', borderRadius: '6px', overflow: 'hidden' }}>
      {/* Header */}
      <Box sx={{ px: 1.5, py: 1, bgcolor: '#1a2332', borderBottom: '1px solid #2a3442' }}>
        <Typography variant="body2" sx={{ fontWeight: 'bold', color: '#ccc' }}>
          Call State Timeline
        </Typography>
      </Box>

      <Box
        ref={scrollRef}
        sx={{
          p: 1.25,
          fontFamily: 'monospace',
          fontSize: '0.6875rem',
          maxHeight: 200,
          overflowY: 'auto',
        }}
      >
        {entries.length === 0 && (
          <Typography variant="caption" sx={{ color: '#666', display: 'block', textAlign: 'center', mt: 2 }}>
            No timeline events yet
          </Typography>
        )}
        {entries.map((entry) => {
          const color = getEventColor(entry.eventType);
          return (
            <Box key={entry.id} sx={{ py: 0.25, display: 'flex', gap: 1, alignItems: 'baseline' }}>
              <Box component="span" sx={{ color: '#666', flexShrink: 0 }}>
                {formatTime(entry.timestamp)}
              </Box>
              <Box component="span" sx={{ color, fontWeight: 'bold', flexShrink: 0 }}>
                {entry.eventType.toUpperCase()}
              </Box>
              <Box component="span" sx={{ color: '#888' }}>
                {entry.description}
              </Box>
            </Box>
          );
        })}
      </Box>
    </Box>
  );
}
