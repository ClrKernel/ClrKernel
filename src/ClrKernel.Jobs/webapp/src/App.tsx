import { useState } from 'react';
import { NavLink, Route, Routes } from 'react-router-dom';
import { apiKey, setApiKey } from './api';
import { Channels } from './pages/Channels';
import { Dashboard } from './pages/Dashboard';
import { JobDetail } from './pages/JobDetail';
import { Jobs } from './pages/Jobs';
import { Notebooks } from './pages/Notebooks';
import { RunDetail } from './pages/RunDetail';

/** Lets the user store the API key when the server requires one. */
function ApiKeyBox() {
  const [key, setKey] = useState(apiKey());
  const [saved, setSaved] = useState(false);
  return (
    <div className="api-key">
      <input
        type="password"
        placeholder="API key (if required)"
        value={key}
        onChange={(e) => {
          setKey(e.target.value);
          setSaved(false);
        }}
      />
      <button
        className="button"
        onClick={() => {
          setApiKey(key);
          setSaved(true);
        }}
      >
        {saved ? 'Saved' : 'Set'}
      </button>
    </div>
  );
}

export function App() {
  return (
    <div className="app">
      <nav className="nav">
        <span className="brand">ClrKernel Jobs</span>
        <NavLink to="/">Dashboard</NavLink>
        <NavLink to="/jobs">Jobs</NavLink>
        <NavLink to="/notebooks">Notebooks</NavLink>
        <NavLink to="/channels">Channels</NavLink>
        <div className="spacer" />
        <ApiKeyBox />
      </nav>
      <main className="main">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/jobs" element={<Jobs />} />
          <Route path="/jobs/new" element={<JobDetail />} />
          <Route path="/jobs/:name" element={<JobDetail />} />
          <Route path="/notebooks" element={<Notebooks />} />
          <Route path="/channels" element={<Channels />} />
          <Route path="/runs/:id" element={<RunDetail />} />
          <Route path="*" element={<p className="muted">Not found.</p>} />
        </Routes>
      </main>
    </div>
  );
}
