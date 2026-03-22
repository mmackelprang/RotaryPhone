import { useState } from 'react';
import {
  Box,
  Typography,
  Button,
  Collapse,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  CircularProgress,
} from '@mui/material';
import type { Ht801HealthStatus } from '../../hooks/useDiagnostics.ts';
import { testRing, getHt801Config, validateHt801 } from '../../services/api.ts';

interface Ht801HealthPanelProps {
  health: Ht801HealthStatus;
}

interface ConfigParam {
  name: string;
  expected: string;
  actual: string;
  matches: boolean;
}

interface ValidationResult {
  isValid: boolean;
  parameters: ConfigParam[];
}

function formatTimeSince(isoDate: string | null): string {
  if (!isoDate) return 'Never';
  const d = new Date(isoDate);
  const diff = Math.floor((Date.now() - d.getTime()) / 1000);
  const timeStr = d.toLocaleTimeString('en-US', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' });
  if (diff < 60) return `${timeStr} (${diff}s ago)`;
  if (diff < 3600) return `${timeStr} (${Math.floor(diff / 60)}m ago)`;
  return `${timeStr} (${Math.floor(diff / 3600)}h ago)`;
}

export default function Ht801HealthPanel({ health }: Ht801HealthPanelProps) {
  const [configExpanded, setConfigExpanded] = useState(false);
  const [configParams, setConfigParams] = useState<ConfigParam[]>([]);
  const [actionLoading, setActionLoading] = useState<string | null>(null);
  const [actionResult, setActionResult] = useState<string | null>(null);

  const rows: Array<{ label: string; value: string; color?: string }> = [
    {
      label: 'SIP Registration',
      value: health.isRegistered
        ? `Active${health.registrationExpiry ? ` (expires in ${health.registrationExpiry}s)` : ''}`
        : 'Not registered',
      color: health.isRegistered ? '#2ecc71' : '#e74c3c',
    },
    {
      label: 'Last REGISTER',
      value: formatTimeSince(health.lastRegisterTime),
    },
    {
      label: 'Network Ping',
      value: health.pingMs != null ? `${health.pingMs}ms` : 'N/A',
      color: health.pingMs != null ? '#2ecc71' : '#888',
    },
    { label: 'Codec', value: health.codec ?? 'N/A' },
    { label: 'Hook State', value: health.hookState ?? 'Unknown' },
    { label: 'Firmware', value: health.firmware ?? 'Unknown' },
  ];

  const handleTestRing = async () => {
    setActionLoading('ring');
    setActionResult(null);
    try {
      await testRing();
      setActionResult('Test ring sent');
    } catch {
      setActionResult('Test ring failed');
    }
    setActionLoading(null);
  };

  const handleValidate = async (autoFix = false) => {
    setActionLoading(autoFix ? 'fix' : 'validate');
    setActionResult(null);
    try {
      const res = await validateHt801(autoFix);
      const result = res.data as ValidationResult;
      if (result.parameters) {
        setConfigParams(result.parameters);
        setConfigExpanded(true);
      }
      setActionResult(
        result.isValid
          ? 'Config valid'
          : autoFix
            ? 'Fixes applied'
            : `${result.parameters?.filter((p) => !p.matches).length ?? 0} mismatches found`
      );
    } catch {
      setActionResult('Validation failed');
    }
    setActionLoading(null);
  };

  const handleReadConfig = async () => {
    setActionLoading('read');
    setActionResult(null);
    try {
      const res = await getHt801Config();
      const params: ConfigParam[] = Object.entries(res.data as Record<string, string>).map(([name, actual]) => ({
        name,
        expected: '',
        actual: String(actual),
        matches: true,
      }));
      setConfigParams(params);
      setConfigExpanded(true);
      setActionResult(`Read ${params.length} parameters`);
    } catch {
      setActionResult('Read config failed');
    }
    setActionLoading(null);
  };

  const hasMismatches = configParams.some((p) => !p.matches);

  return (
    <Box sx={{ bgcolor: '#111827', borderRadius: '6px', overflow: 'hidden' }}>
      {/* Header */}
      <Box sx={{ px: 1.5, py: 1, bgcolor: '#1a2332', borderBottom: '1px solid #2a3442' }}>
        <Typography variant="body2" sx={{ fontWeight: 'bold', color: '#ccc' }}>
          HT801 Device Health
        </Typography>
      </Box>

      {/* Status rows */}
      <Box sx={{ p: 1.25, fontSize: '0.75rem' }}>
        {rows.map((row) => (
          <Box
            key={row.label}
            sx={{ display: 'flex', justifyContent: 'space-between', py: 0.375 }}
          >
            <Typography variant="caption" sx={{ color: '#888' }}>
              {row.label}
            </Typography>
            <Typography variant="caption" sx={{ color: row.color ?? '#ccc' }}>
              {row.value}
            </Typography>
          </Box>
        ))}

        {/* Action buttons */}
        <Box sx={{ mt: 1, display: 'flex', gap: 0.75, flexWrap: 'wrap', alignItems: 'center' }}>
          <Button
            size="small"
            variant="outlined"
            disabled={actionLoading !== null}
            onClick={handleTestRing}
            sx={{
              fontSize: '0.625rem',
              py: 0.25,
              px: 1.25,
              color: '#4a9eff',
              borderColor: '#4a9eff',
              bgcolor: '#2a3442',
              minWidth: 0,
            }}
          >
            {actionLoading === 'ring' ? <CircularProgress size={12} /> : 'Test Ring'}
          </Button>
          <Button
            size="small"
            variant="outlined"
            disabled={actionLoading !== null}
            onClick={() => handleValidate(false)}
            sx={{
              fontSize: '0.625rem',
              py: 0.25,
              px: 1.25,
              color: '#e67e22',
              borderColor: '#e67e22',
              bgcolor: '#2a3442',
              minWidth: 0,
            }}
          >
            {actionLoading === 'validate' ? <CircularProgress size={12} /> : 'Validate Config'}
          </Button>
          <Button
            size="small"
            variant="outlined"
            disabled={actionLoading !== null}
            onClick={handleReadConfig}
            sx={{
              fontSize: '0.625rem',
              py: 0.25,
              px: 1.25,
              color: '#888',
              borderColor: '#555',
              bgcolor: '#2a3442',
              minWidth: 0,
            }}
          >
            {actionLoading === 'read' ? <CircularProgress size={12} /> : 'Read Config'}
          </Button>
          {actionResult && (
            <Typography variant="caption" sx={{ color: '#f1c40f', ml: 0.5 }}>
              {actionResult}
            </Typography>
          )}
        </Box>

        {/* Config comparison table */}
        <Collapse in={configExpanded}>
          <Box sx={{ mt: 1, maxHeight: 200, overflowY: 'auto' }}>
            <Table size="small" sx={{ '& td, & th': { fontSize: '0.625rem', py: 0.25, px: 1, borderColor: '#2a3442' } }}>
              <TableHead>
                <TableRow>
                  <TableCell sx={{ color: '#888' }}>Parameter</TableCell>
                  <TableCell sx={{ color: '#888' }}>Expected</TableCell>
                  <TableCell sx={{ color: '#888' }}>Actual</TableCell>
                  <TableCell sx={{ color: '#888' }}>Status</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {configParams.map((p) => (
                  <TableRow key={p.name}>
                    <TableCell sx={{ color: '#ccc' }}>{p.name}</TableCell>
                    <TableCell sx={{ color: '#888' }}>{p.expected || '-'}</TableCell>
                    <TableCell sx={{ color: '#ccc' }}>{p.actual}</TableCell>
                    <TableCell sx={{ color: p.matches ? '#2ecc71' : '#e74c3c' }}>
                      {p.matches ? 'OK' : 'MISMATCH'}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {hasMismatches && (
              <Box sx={{ mt: 0.75, display: 'flex', justifyContent: 'flex-end' }}>
                <Button
                  size="small"
                  variant="outlined"
                  disabled={actionLoading !== null}
                  onClick={() => handleValidate(true)}
                  sx={{
                    fontSize: '0.625rem',
                    py: 0.25,
                    px: 1.25,
                    color: '#e74c3c',
                    borderColor: '#e74c3c',
                    bgcolor: '#2a3442',
                    minWidth: 0,
                  }}
                >
                  {actionLoading === 'fix' ? <CircularProgress size={12} /> : 'Fix All Mismatches'}
                </Button>
              </Box>
            )}
          </Box>
        </Collapse>
      </Box>
    </Box>
  );
}
