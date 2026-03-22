import axios from 'axios';

const api = axios.create({
  baseURL: 'http://localhost:5555/api',
  headers: {
    'Content-Type': 'application/json',
  },
});

export const getDiagnosticsStatus = () => api.get('/diagnostics/status');
export const getSipLog = (count = 50, method?: string) =>
  api.get('/diagnostics/sip-log', { params: { count, method } });
export const getTimeline = (count = 50) =>
  api.get('/diagnostics/timeline', { params: { count } });
export const testRing = () => api.post('/diagnostics/test-ring');
export const getHt801Config = () => api.get('/diagnostics/ht801/config');
export const validateHt801 = (autoFix = false) =>
  api.post('/diagnostics/ht801/validate', null, { params: { autoFix } });

export default api;
