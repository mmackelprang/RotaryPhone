import { useEffect } from 'react';
import { Routes, Route } from 'react-router-dom';
import { Layout } from './components/Layout';
import { signalRService } from './services/SignalRService';
import Dashboard from './pages/Dashboard';
import Contacts from './pages/Contacts';
import CallHistory from './pages/CallHistory';

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
      </Routes>
    </Layout>
  );
}

export default App;