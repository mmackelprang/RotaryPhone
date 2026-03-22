import { Box, Card, CardContent, Typography } from '@mui/material';
import type { Ht801HealthStatus, GvBridgeStatus, SipServerStatus } from '../../hooks/useDiagnostics.ts';

interface StatusBarProps {
  ht801Health: Ht801HealthStatus;
  sipServer: SipServerStatus;
  gvBridgeStatus: GvBridgeStatus;
  callState: string;
}

interface StatusCardProps {
  label: string;
  statusText: string;
  detail: string;
  color: string;
}

function StatusCard({ label, statusText, detail, color }: StatusCardProps) {
  return (
    <Card
      sx={{
        flex: 1,
        borderLeft: `3px solid ${color}`,
        bgcolor: '#1a2332',
        minWidth: 0,
      }}
    >
      <CardContent sx={{ py: 1.5, px: 2, '&:last-child': { pb: 1.5 } }}>
        <Typography
          variant="caption"
          sx={{ color: '#888', textTransform: 'uppercase', fontSize: '0.65rem', letterSpacing: '0.05em' }}
        >
          {label}
        </Typography>
        <Typography
          variant="body2"
          sx={{ color, fontWeight: 'bold', mt: 0.25 }}
        >
          {statusText}
        </Typography>
        <Typography
          variant="caption"
          sx={{ color: '#666', fontSize: '0.65rem', display: 'block', mt: 0.25 }}
        >
          {detail}
        </Typography>
      </CardContent>
    </Card>
  );
}

function formatTimeSince(isoDate: string | null): string {
  if (!isoDate) return 'Never';
  const diff = Math.floor((Date.now() - new Date(isoDate).getTime()) / 1000);
  if (diff < 60) return `${diff}s ago`;
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  return `${Math.floor(diff / 3600)}h ago`;
}

export default function StatusBar({ ht801Health, sipServer, gvBridgeStatus, callState }: StatusBarProps) {
  const ht801Color = ht801Health.isRegistered ? '#2ecc71' : '#e74c3c';
  const ht801Status = ht801Health.isRegistered ? 'Connected' : 'Disconnected';
  const ht801Detail = [
    ht801Health.ipAddress,
    ht801Health.isRegistered ? 'Registered' : 'Not registered',
    ht801Health.lastRegisterTime ? `Last seen ${formatTimeSince(ht801Health.lastRegisterTime)}` : null,
  ]
    .filter(Boolean)
    .join(' \u00b7 ');

  const sipColor = sipServer.isListening ? '#2ecc71' : '#e74c3c';
  const sipStatus = sipServer.isListening
    ? `Listening :${sipServer.listenPort ?? 5060}`
    : 'Not listening';
  const sipDetail = sipServer.listenAddress
    ? `${sipServer.listenAddress}:${sipServer.listenPort ?? 5060} UDP`
    : 'No server info';

  const gvColor = gvBridgeStatus.extensionConnected ? '#2ecc71' : '#e74c3c';
  const gvStatus = gvBridgeStatus.extensionConnected ? 'Extension Connected' : 'Disconnected';
  const gvDetail = [
    gvBridgeStatus.extensionVersion ? `v${gvBridgeStatus.extensionVersion}` : null,
    `Mode: ${gvBridgeStatus.activeMode}`,
  ]
    .filter(Boolean)
    .join(' \u00b7 ');

  const callColor = callState === 'Idle' ? '#888' : callState === 'Ringing' ? '#f1c40f' : '#2ecc71';
  const callDetail =
    callState === 'Idle'
      ? 'Phone on-hook \u00b7 No active call'
      : callState === 'Ringing'
        ? 'Incoming call'
        : callState === 'InCall'
          ? 'Call in progress'
          : callState;

  return (
    <Box sx={{ display: 'flex', gap: 1.25, mb: 2 }}>
      <StatusCard label="HT801" statusText={ht801Status} detail={ht801Detail} color={ht801Color} />
      <StatusCard label="SIP Server" statusText={sipStatus} detail={sipDetail} color={sipColor} />
      <StatusCard label="GV Bridge" statusText={gvStatus} detail={gvDetail} color={gvColor} />
      <StatusCard label="Call State" statusText={callState} detail={callDetail} color={callColor} />
    </Box>
  );
}
