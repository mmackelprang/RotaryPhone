import { useState, useEffect, useRef } from 'react';
import { Box, Typography, Chip } from '@mui/material';
import type { SipMessageEntry, DiagnosisAlert } from '../../hooks/useDiagnostics.ts';

interface SipMessageLogProps {
  messages: SipMessageEntry[];
  diagnoses: DiagnosisAlert[];
}

const METHOD_COLORS: Record<string, string> = {
  INVITE: '#4a9eff',
  REGISTER: '#e67e22',
  NOTIFY: '#2ecc71',
  BYE: '#e74c3c',
  ACK: '#9b59b6',
  CANCEL: '#e74c3c',
  OPTIONS: '#888',
};

const FILTER_METHODS = ['INVITE', 'REGISTER', 'NOTIFY', 'BYE', 'ALL'] as const;

function formatTime(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' });
  } catch {
    return iso;
  }
}

function getMethodColor(method: string): string {
  return METHOD_COLORS[method.toUpperCase()] ?? '#888';
}

function isFailedStatus(code: number | null): boolean {
  return code !== null && code >= 400;
}

export default function SipMessageLog({ messages, diagnoses }: SipMessageLogProps) {
  const [filter, setFilter] = useState<string>('ALL');
  const scrollRef = useRef<HTMLDivElement>(null);
  const userScrolled = useRef(false);

  const filteredMessages = filter === 'ALL'
    ? messages
    : messages.filter((m) => m.method.toUpperCase() === filter);

  // Auto-scroll to bottom when new messages arrive, unless user has scrolled up
  useEffect(() => {
    const el = scrollRef.current;
    if (el && !userScrolled.current) {
      el.scrollTop = el.scrollHeight;
    }
  }, [filteredMessages.length]);

  const handleScroll = () => {
    const el = scrollRef.current;
    if (el) {
      const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
      userScrolled.current = !atBottom;
    }
  };

  return (
    <Box sx={{ bgcolor: '#111827', borderRadius: '6px', overflow: 'hidden', display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Header */}
      <Box
        sx={{
          px: 1.5,
          py: 1,
          bgcolor: '#1a2332',
          borderBottom: '1px solid #2a3442',
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          flexShrink: 0,
        }}
      >
        <Typography variant="body2" sx={{ fontWeight: 'bold', color: '#ccc' }}>
          SIP Message Log
        </Typography>
        <Box sx={{ display: 'flex', gap: 0.75 }}>
          {FILTER_METHODS.map((m) => (
            <Chip
              key={m}
              label={m}
              size="small"
              onClick={() => setFilter(m)}
              sx={{
                fontSize: '0.625rem',
                height: 20,
                bgcolor: filter === m ? (m === 'ALL' ? '#444' : getMethodColor(m) + '33') : '#2a3442',
                color: m === 'ALL' ? '#888' : getMethodColor(m),
                border: filter === m ? `1px solid ${m === 'ALL' ? '#666' : getMethodColor(m)}` : 'none',
                cursor: 'pointer',
                '&:hover': { opacity: 0.8 },
              }}
            />
          ))}
        </Box>
      </Box>

      {/* Messages */}
      <Box
        ref={scrollRef}
        onScroll={handleScroll}
        sx={{
          p: 1,
          fontFamily: 'monospace',
          fontSize: '0.6875rem',
          overflowY: 'auto',
          flexGrow: 1,
          minHeight: 0,
        }}
      >
        {filteredMessages.length === 0 && (
          <Typography variant="caption" sx={{ color: '#666', display: 'block', textAlign: 'center', mt: 4 }}>
            No SIP messages yet
          </Typography>
        )}
        {filteredMessages.map((msg) => {
          const failed = msg.isFailed || isFailedStatus(msg.statusCode);
          const methodColor = getMethodColor(msg.method);
          const dirArrow = msg.direction === 'Received' ? '\u2190' : '\u2192';
          // Find matching diagnosis for this message's callId
          const relatedDiagnosis = msg.callId
            ? diagnoses.find((d) => d.relatedCallId === msg.callId)
            : null;

          return (
            <Box key={msg.id}>
              <Box
                sx={{
                  py: 0.5,
                  borderBottom: '1px solid #1a2332',
                  bgcolor: failed ? '#1a0a0a' : 'transparent',
                  display: 'flex',
                  flexWrap: 'wrap',
                  alignItems: 'baseline',
                  gap: 0.5,
                }}
              >
                <Box component="span" sx={{ color: '#666' }}>
                  {formatTime(msg.timestamp)}
                </Box>
                <Box component="span" sx={{ color: methodColor, mx: 0.5 }}>
                  {dirArrow}
                </Box>
                <Box component="span" sx={{ color: methodColor, fontWeight: 'bold' }}>
                  {msg.method}
                </Box>
                <Box component="span" sx={{ color: '#888' }}>
                  {msg.direction === 'Received' ? `from ${msg.fromAddress}` : msg.toAddress ? `sip:${msg.toAddress}` : ''}
                </Box>
                {msg.statusCode != null && (
                  <Box
                    component="span"
                    sx={{
                      color: msg.statusCode >= 400 ? '#e74c3c' : msg.statusCode >= 200 ? '#2ecc71' : '#f1c40f',
                      ml: 1,
                    }}
                  >
                    {msg.direction === 'Sent' ? '\u2190' : '\u2192'} {msg.statusCode} {msg.statusReason ?? ''}
                  </Box>
                )}
                {msg.summary && (
                  <Box component="span" sx={{ color: '#666', ml: 1, fontSize: '0.625rem' }}>
                    {msg.summary}
                  </Box>
                )}
              </Box>
              {/* Diagnosis annotation */}
              {failed && relatedDiagnosis && (
                <Box
                  sx={{
                    py: 0.5,
                    pl: 2.5,
                    borderBottom: '1px solid #1a2332',
                    bgcolor: '#1a1a0a',
                  }}
                >
                  <Box component="span" sx={{ color: '#e74c3c', fontWeight: 'bold' }}>
                    ! DIAGNOSIS
                  </Box>
                  <Box component="span" sx={{ color: '#e74c3c', ml: 1 }}>
                    {relatedDiagnosis.issue}
                  </Box>
                  {relatedDiagnosis.suggestions.length > 0 && (
                    <Box sx={{ color: '#f1c40f', ml: 2.5, mt: 0.25, fontSize: '0.625rem' }}>
                      Check: {relatedDiagnosis.suggestions.join(' ')}
                    </Box>
                  )}
                </Box>
              )}
            </Box>
          );
        })}
      </Box>
    </Box>
  );
}
