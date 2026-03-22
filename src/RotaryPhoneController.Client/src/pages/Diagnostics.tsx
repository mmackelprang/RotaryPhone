import { Box, CircularProgress, Typography } from '@mui/material';
import { useDiagnostics } from '../hooks/useDiagnostics.ts';
import StatusBar from '../components/diagnostics/StatusBar.tsx';
import SipMessageLog from '../components/diagnostics/SipMessageLog.tsx';
import Ht801HealthPanel from '../components/diagnostics/Ht801HealthPanel.tsx';
import RtpStatsPanel from '../components/diagnostics/RtpStatsPanel.tsx';
import CallTimeline from '../components/diagnostics/CallTimeline.tsx';

export default function Diagnostics() {
  const {
    sipMessages,
    timeline,
    ht801Health,
    diagnoses,
    gvBridgeStatus,
    rtpStats,
    sipServer,
    callState,
    loading,
  } = useDiagnostics();

  if (loading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: 400 }}>
        <CircularProgress />
      </Box>
    );
  }

  return (
    <Box sx={{ p: 1 }}>
      <Typography variant="h6" sx={{ mb: 1.5, color: 'text.primary' }}>
        SIP &amp; Call Diagnostics
      </Typography>

      {/* Status Bar - full width */}
      <StatusBar
        ht801Health={ht801Health}
        sipServer={sipServer}
        gvBridgeStatus={gvBridgeStatus}
        callState={callState}
      />

      {/* Two-column layout */}
      <Box
        sx={{
          display: 'flex',
          gap: 1.5,
          minHeight: 0,
          height: 'calc(100vh - 280px)',
        }}
      >
        {/* Left: SIP Message Log (~60%) */}
        <Box sx={{ flex: 3, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
          <SipMessageLog messages={sipMessages} diagnoses={diagnoses} />
        </Box>

        {/* Right: Stacked panels (~40%) */}
        <Box
          sx={{
            flex: 2,
            display: 'flex',
            flexDirection: 'column',
            gap: 1.25,
            minWidth: 0,
            overflowY: 'auto',
          }}
        >
          <Ht801HealthPanel health={ht801Health} />
          <RtpStatsPanel stats={rtpStats} />
          <CallTimeline entries={timeline} />
        </Box>
      </Box>
    </Box>
  );
}
