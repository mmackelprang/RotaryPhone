import { useGVBridge } from '../hooks/useGVBridge';

export default function GVBridge() {
  const { status, smsMessages, modes, switchMode, sendSms } = useGVBridge();

  return (
    <div style={{ padding: '1rem' }}>
      <h2>GV Bridge</h2>

      {/* Connection Mode Selector */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Call Path</h3>
        <div style={{ display: 'flex', gap: '1rem' }}>
          {['BluetoothHfp', 'SipTrunk', 'GVBrowser'].map(mode => (
            <label key={mode} style={{ cursor: 'pointer' }}>
              <input
                type="radio"
                name="callMode"
                value={mode}
                checked={status.activeMode === mode}
                onChange={() => switchMode(mode)}
              />
              {' '}{mode === 'BluetoothHfp' ? 'Bluetooth Phone' : mode === 'SipTrunk' ? 'SIP Trunk' : 'GV Browser'}
            </label>
          ))}
        </div>
      </div>

      {/* Bridge Status */}
      <div style={{ marginBottom: '1rem', padding: '1rem', border: '1px solid #ccc', borderRadius: 8 }}>
        <h3>Bridge Status</h3>
        <p>
          Extension:{' '}
          <span style={{ color: status.extensionConnected ? 'green' : 'red', fontWeight: 'bold' }}>
            {status.extensionConnected ? 'Connected' : 'Disconnected'}
          </span>
          {status.extensionVersion && <span style={{ color: '#888', marginLeft: 8 }}>v{status.extensionVersion}</span>}
        </p>
        <p>Active Mode: <strong>{status.activeMode}</strong></p>
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
