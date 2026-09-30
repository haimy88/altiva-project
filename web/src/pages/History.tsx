import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { api, ApiError, isFinal, type JobListItem, type Paged } from '../api';
import { StatusBadge } from '../components/StatusBadge';
import { formatDuration, formatTime } from '../format';

const PAGE_SIZE = 10;
const REFRESH_MS = 3000;

export function History() {
  const [params, setParams] = useSearchParams();
  const page = Math.max(1, Number(params.get('page')) || 1); // page lives in the URL so back/refresh keep it
  const [data, setData] = useState<Paged<JobListItem> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true); // first load / page change only, not background refreshes

  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;

    setLoading(true);

    async function load() {
      let refresh = true; // after an error, try again
      try {
        const result = await api.listJobs(page, PAGE_SIZE);
        if (!active) return;
        setData(result);
        setError(null);
        // Keep statuses fresh while any job on this page is still in progress.
        refresh = result.items.some(j => !isFinal(j.status));
      } catch (err) {
        if (!active) return;
        setError(err instanceof Error ? err.message : 'Failed to load history');
        if (err instanceof ApiError && err.status >= 400 && err.status < 500) refresh = false; // bad request: retrying won't help
      }
      setLoading(false);
      if (refresh) timer = setTimeout(load, REFRESH_MS);
    }

    load();
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [page]);

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;

  return (
    <section className="card">
      <div className="section-header">
        <h1>History</h1>
        {data && <span className="muted">{data.totalCount} jobs</span>}
      </div>

      {error && <p className="alert" role="alert">{error}</p>}
      {!data && loading && <p className="muted loading">Loading…</p>}

      {data && data.totalCount === 0 && (
        <p className="muted">No crawls yet. <Link to="/">Start one</Link>.</p>
      )}

      {data && data.totalCount > 0 && data.items.length === 0 && (
        <p className="muted">There is no page {page}. <Link to="/history">Go to the newest jobs</Link>.</p>
      )}

      {data && data.items.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>URL</th>
                <th>Status</th>
                <th>Created</th>
                <th>Duration</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map(job => (
                <tr key={job.jobId}>
                  <td className="url-cell">
                    <Link to={`/jobs/${job.jobId}`} title={job.url}>{job.url}</Link>
                  </td>
                  <td><StatusBadge status={job.status} /></td>
                  <td>{formatTime(job.createdAt)}</td>
                  <td>{formatDuration(job.startedAt, job.completedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {data && totalPages > 1 && (
        <div className="pager">
          <button disabled={page <= 1 || loading} onClick={() => setParams({ page: String(page - 1) })}>← Newer</button>
          <span className="muted">Page {page} of {totalPages}</span>
          <button disabled={page >= totalPages || loading} onClick={() => setParams({ page: String(page + 1) })}>Older →</button>
        </div>
      )}
    </section>
  );
}
