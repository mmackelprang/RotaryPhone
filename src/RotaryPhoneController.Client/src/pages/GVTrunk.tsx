import { useState } from 'react';
import { useGVTrunk } from '../hooks/useGVTrunk';

export default function GVTrunk() {
  const { status, calls, smsMessages, dial, forceReregister, refreshCalls } = useGVTrunk();
  const [dialNumber, setDialNumber] = useState('');

  return (
    <div style={{ padding: '1rem' }}>
      <h2>Google Voice Trunk</h2>

      {/* Status Panel */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Trunk Status</h3>
        <p>
          Registration:{' '}
          <span style={{ color: status.isRegistered ? 'green' : 'red', fontWeight: 'bold' }}>
            {status.isRegistered ? 'Registered' : 'Unregistered'}
          </span>
        </p>
        <p>Call State: <strong>{status.callState}</strong></p>
        {status.activeCallDurationSeconds > 0 && (
          <p>Duration: {status.activeCallDurationSeconds}s</p>
        )}
        <button onClick={forceReregister}>Force Re-Register</button>
      </div>

      {/* Dial Panel */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Outbound Dial</h3>
        <input
          type="tel"
          placeholder="+1XXXXXXXXXX"
          value={dialNumber}
          onChange={e => setDialNumber(e.target.value)}
          style={{ marginRight: 8, padding: '0.25rem' }}
        />
        <button
          onClick={() => { dial(dialNumber); setDialNumber(''); }}
          disabled={!status.isRegistered || !dialNumber}
        >
          Dial via GV Trunk
        </button>
      </div>

      {/* Call History */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Call History <button onClick={refreshCalls} style={{ fontSize: '0.8em' }}>Refresh</button></h3>
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr>
              <th style={{ textAlign: 'left' }}>Time</th>
              <th style={{ textAlign: 'left' }}>Direction</th>
              <th style={{ textAlign: 'left' }}>Number</th>
              <th style={{ textAlign: 'left' }}>Status</th>
              <th style={{ textAlign: 'left' }}>Duration</th>
            </tr>
          </thead>
          <tbody>
            {calls.map(c => (
              <tr key={c.id}>
                <td>{new Date(c.startedAt).toLocaleString()}</td>
                <td>{c.direction}</td>
                <td>{c.remoteNumber}</td>
                <td>{c.status}</td>
                <td>{c.durationSeconds != null ? `${c.durationSeconds}s` : '-'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* SMS Notifications */}
      <div style={{ padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>SMS / Missed Calls</h3>
        {smsMessages.length === 0 ? (
          <p style={{ color: '#888' }}>No notifications yet</p>
        ) : (
          smsMessages.map((s, i) => (
            <div key={i} style={{ marginBottom: '0.5rem', padding: '0.5rem', background: '#f5f5f5', borderRadius: 4 }}>
              <strong>{s.type === 'Sms' ? 'SMS' : 'Missed Call'}</strong> from {s.fromNumber}
              <span style={{ color: '#888', marginLeft: 8 }}>{new Date(s.receivedAt).toLocaleTimeString()}</span>
              {s.body && <p style={{ margin: '0.25rem 0 0' }}>{s.body}</p>}
            </div>
          ))
        )}
      </div>
    </div>
  );
}
