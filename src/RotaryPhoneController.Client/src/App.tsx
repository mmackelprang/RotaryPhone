import { useEffect } from 'react';
import { Routes, Route } from 'react-router-dom';
import { Layout } from './components/Layout';
import { signalRService } from './services/SignalRService';
import Dashboard from './pages/Dashboard';
import Contacts from './pages/Contacts';
import CallHistory from './pages/CallHistory';
import Pairing from './pages/Pairing';
import GVTrunk from './pages/GVTrunk';

function App() {
  useEffect(() => {
    // Start SignalR connection on app mount
    signalRService.start();
    return () => {
      signalRService.stop();
    };
  }, []);

  return (
    <Layout>
      <Routes>
        <Route path="/" element={<Dashboard />} />
        <Route path="/contacts" element={<Contacts />} />
        <Route path="/history" element={<CallHistory />} />
        <Route path="/pairing" element={<Pairing />} />
        <Route path="/gvtrunk" element={<GVTrunk />} />
      </Routes>
    </Layout>
  );
}

export default App;