import { NavLink, Route, Routes, useParams } from 'react-router';
import { History } from './pages/History';
import { JobDetails } from './pages/JobDetails';
import { StartCrawl } from './pages/StartCrawl';

/** key = jobId: going from one job to another mounts a fresh page (no leftover data, polling or tree state). */
function JobDetailsRoute() {
  const { jobId = '' } = useParams();
  return <JobDetails key={jobId} jobId={jobId} />;
}

export function App() {
  return (
    <>
      <header className="topbar">
        <span className="brand">🕷 Crawler</span>
        <nav>
          <NavLink to="/" end>Start crawl</NavLink>
          <NavLink to="/history">History</NavLink>
        </nav>
      </header>
      <main>
        <Routes>
          <Route path="/" element={<StartCrawl />} />
          <Route path="/jobs/:jobId" element={<JobDetailsRoute />} />
          <Route path="/history" element={<History />} />
          <Route path="*" element={<p className="card">Page not found.</p>} />
        </Routes>
      </main>
    </>
  );
}
