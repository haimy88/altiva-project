import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { api, ApiError, isFinal, type JobSummary, type PageNode } from '../api';
import { PageTree } from '../components/PageTree';
import { StatusBadge } from '../components/StatusBadge';
import { formatDuration, formatTime } from '../format';

const POLL_MS = 1500;
const TREE_REFRESH_MS = 4000; // the tree is the heavy call, so refresh it less often while running
const SETTLE_MS = 30_000; // after a cancel the worker skips leftover pages on its next step; keep polling a bit for that

export function JobDetails({ jobId }: { jobId: string }) {
  const [summary, setSummary] = useState<JobSummary | null>(null);
  const [tree, setTree] = useState<PageNode | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [canceling, setCanceling] = useState(false);
  const [cancelError, setCancelError] = useState<string | null>(null); // separate: polling mustn't clear it
  const [reloadKey, setReloadKey] = useState(0);

  // Polling loop: the next request is scheduled only after the previous one finished, so they never pile up.
  // Stops once the job is final (and its pages have settled). Leaving the page aborts it.
  useEffect(() => {
    const abort = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let lastTreeAt = 0;
    let finalSince: number | null = null;

    async function tick() {
      try {
        const s = await api.getJob(jobId, abort.signal);
        setSummary(s);
        setError(null);

        const final = isFinal(s.status);
        if (final) finalSince ??= Date.now();
        if (final || Date.now() - lastTreeAt >= TREE_REFRESH_MS) {
          setTree((await api.getTree(jobId, abort.signal)).root);
          lastTreeAt = Date.now();
        }

        const settled = s.progress.pagesPending === 0 || Date.now() - finalSince! > SETTLE_MS;
        if (final && settled) return;
      } catch (err) {
        if (abort.signal.aborted) return;
        if (err instanceof ApiError && err.status === 404) {
          setNotFound(true);
          return;
        }
        setError(err instanceof Error ? err.message : 'Failed to load job'); // keep last data, keep retrying
      }
      timer = setTimeout(tick, POLL_MS);
    }

    tick();
    return () => {
      abort.abort();
      clearTimeout(timer);
    };
  }, [jobId, reloadKey]);

  async function cancel() {
    setCanceling(true);
    setCancelError(null);
    try {
      await api.cancelJob(jobId);
    } catch (err) {
      setCancelError(err instanceof Error ? err.message : 'Cancel failed'); // e.g. 409: it finished just before
    } finally {
      setCanceling(false);
      setReloadKey(k => k + 1); // refresh right away (and restart polling if it had stopped)
    }
  }

  if (notFound) {
    return (
      <section className="card">
        <h1>Job not found</h1>
        <p className="muted">No job with id {jobId}.</p>
        <Link to="/history">Back to history</Link>
      </section>
    );
  }

  if (!summary) {
    return (
      <section className="card">
        {error ? <p className="alert" role="alert">{error}</p> : <p className="muted loading">Loading job…</p>}
      </section>
    );
  }

  const running = !isFinal(summary.status);

  return (
    <>
      <section className="card">
        <div className="job-header">
          <div>
            <h1 className="job-url">
              <a href={summary.url} target="_blank" rel="noreferrer">{summary.url}</a>
            </h1>
            <p className="muted mono">Job {summary.jobId}</p>
          </div>
          <div className="job-header-actions" role="status" aria-live="polite">
            <StatusBadge status={summary.status} />
            {running && (
              <button onClick={cancel} disabled={canceling}>{canceling ? 'Canceling…' : 'Cancel'}</button>
            )}
          </div>
        </div>

        {error && <p className="alert" role="alert">{error} — retrying…</p>}
        {cancelError && <p className="alert" role="alert">{cancelError}</p>}
        {summary.failureReason && <p className="alert">{summary.failureReason}</p>}

        <dl className="facts">
          <Fact label="Created" value={formatTime(summary.createdAt)} />
          <Fact label="Started" value={formatTime(summary.startedAt)} />
          <Fact label="Completed" value={formatTime(summary.completedAt)} />
          <Fact label="Duration" value={formatDuration(summary.startedAt, summary.completedAt)} />
          <Fact label="Max depth" value={String(summary.maxDepth)} />
          <Fact label="Max pages" value={String(summary.maxPages)} />
        </dl>

        <Progress summary={summary} />
      </section>

      <section className="card">
        <div className="section-header">
          <h2>Result tree</h2>
          {running && <span className="muted loading">Live: updates while crawling</span>}
        </div>
        {tree ? (
          <PageTree root={tree} />
        ) : (
          <p className="muted">{running ? 'Waiting for the worker to crawl the first page…' : 'No pages were crawled.'}</p>
        )}
      </section>
    </>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

/** Pages processed out of pages discovered so far. The total grows while crawling, so this is "so far", not an ETA. */
function Progress({ summary }: { summary: JobSummary }) {
  const p = summary.progress;
  const processed = p.pagesCrawled + p.pagesFailed + p.pagesSkipped;
  const percent = p.pagesDiscovered === 0 ? 0 : Math.round((processed / p.pagesDiscovered) * 100);

  return (
    <div className="progress">
      <div className="progress-track">
        <div className="progress-fill" style={{ width: `${percent}%` }} />
      </div>
      <p>
        <strong>{processed}</strong> of {p.pagesDiscovered} discovered pages processed
        <span className="muted">
          {' '}· {p.pagesCrawled} crawled · {p.pagesFailed} failed · {p.pagesSkipped} skipped · {p.pagesPending} queued
        </span>
      </p>
      {p.pagesDiscovered >= summary.maxPages && (
        <p className="muted">Reached the {summary.maxPages}-page limit: further links were recorded but not queued.</p>
      )}
    </div>
  );
}
