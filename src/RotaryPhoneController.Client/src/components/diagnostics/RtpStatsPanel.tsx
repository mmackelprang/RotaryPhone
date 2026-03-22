import { Box, Typography } from '@mui/material';
import type { RtpStats } from '../../hooks/useDiagnostics.ts';

interface RtpStatsPanelProps {
  stats: RtpStats;
}

export default function RtpStatsPanel({ stats }: RtpStatsPanelProps) {
  return (
    <Box sx={{ bgcolor: '#111827', borderRadius: '6px', overflow: 'hidden' }}>
      {/* Header */}
      <Box sx={{ px: 1.5, py: 1, bgcolor: '#1a2332', borderBottom: '1px solid #2a3442' }}>
        <Typography variant="body2" sx={{ fontWeight: 'bold', color: '#ccc' }}>
          RTP Audio Stream
        </Typography>
      </Box>

      <Box sx={{ p: 1.25, fontSize: '0.75rem' }}>
        {!stats.isActive ? (
          <Box>
            <Typography variant="caption" sx={{ color: '#888', display: 'block', mb: 0.5 }}>
              No active stream
            </Typography>
            <Box sx={{ color: '#555', fontSize: '0.6875rem' }}>
              <Typography variant="caption" sx={{ color: '#555', display: 'block' }}>
                During active calls, shows:
              </Typography>
              {[
                'Packets sent/received per second',
                'Jitter, packet loss %',
                'Codec / sample rate',
              ].map((item) => (
                <Typography key={item} variant="caption" sx={{ color: '#555', display: 'block', ml: 1 }}>
                  &middot; {item}
                </Typography>
              ))}
            </Box>
          </Box>
        ) : (
          <Box>
            <StatRow label="Packets Sent" value={String(stats.packetsSent)} />
            <StatRow label="Packets Received" value={String(stats.packetsReceived)} />
            <StatRow
              label="Jitter"
              value={`${stats.jitterMs.toFixed(1)}ms`}
              color={stats.jitterMs > 30 ? '#e74c3c' : stats.jitterMs > 15 ? '#f1c40f' : '#2ecc71'}
            />
            <StatRow
              label="Packet Loss"
              value={`${stats.lossPercent.toFixed(1)}%`}
              color={stats.lossPercent > 5 ? '#e74c3c' : stats.lossPercent > 1 ? '#f1c40f' : '#2ecc71'}
            />
            <StatRow label="Codec" value={stats.codec ?? 'Unknown'} />
            {stats.localPort != null && stats.remotePort != null && (
              <StatRow label="Ports" value={`${stats.localPort} <-> ${stats.remotePort}`} />
            )}
          </Box>
        )}
      </Box>
    </Box>
  );
}

function StatRow({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <Box sx={{ display: 'flex', justifyContent: 'space-between', py: 0.375 }}>
      <Typography variant="caption" sx={{ color: '#888' }}>
        {label}
      </Typography>
      <Typography variant="caption" sx={{ color: color ?? '#ccc' }}>
        {value}
      </Typography>
    </Box>
  );
}
