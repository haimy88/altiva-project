import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { api, ApiError } from '../api';

export function StartCrawl() {
  const navigate = useNavigate();
  const [url, setUrl] = useState('');
  const [maxDepth, setMaxDepth] = useState('2');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    setFieldErrors({});
    // The API owns the validation rules; this only catches what can't even be sent as an int (e.g. "2.5").
    if (maxDepth !== '' && !Number.isInteger(Number(maxDepth))) {
      setFieldErrors({ maxDepth: ['Must be a whole number'] });
      setSubmitting(false);
      return;
    }
    try {
      const { jobId } = await api.createJob(url.trim(), maxDepth === '' ? undefined : Number(maxDepth));
      navigate(`/jobs/${jobId}`);
    } catch (err) {
      // The API is the single source of validation rules; we just show what it says next to each field.
      if (err instanceof ApiError && Object.keys(err.fieldErrors).length > 0) setFieldErrors(err.fieldErrors);
      else setError(err instanceof Error ? err.message : 'Something went wrong');
      setSubmitting(false);
    }
  }

  return (
    <section className="card narrow">
      <h1>Start a crawl</h1>
      <p className="muted">Crawls same-domain HTML pages breadth-first (up to 200 pages) and reports each page's domain link ratio.</p>

      <form onSubmit={onSubmit} noValidate>
        <label>
          <span>Start URL</span>
          <input
            type="url"
            placeholder="https://quotes.toscrape.com/"
            value={url}
            onChange={e => setUrl(e.target.value)}
            aria-invalid={!!fieldErrors.url}
            aria-describedby={fieldErrors.url ? 'url-error' : undefined}
            required
            autoFocus
          />
        </label>
        {fieldErrors.url && <p id="url-error" className="field-error" role="alert">{fieldErrors.url.join(' ')}</p>}

        <label>
          <span>Max depth <span className="muted">(0–5, default 2)</span></span>
          <input
            type="number"
            min={0}
            max={5}
            value={maxDepth}
            onChange={e => setMaxDepth(e.target.value)}
            aria-invalid={!!fieldErrors.maxDepth}
            aria-describedby={fieldErrors.maxDepth ? 'depth-error' : undefined}
          />
        </label>
        {fieldErrors.maxDepth && <p id="depth-error" className="field-error" role="alert">{fieldErrors.maxDepth.join(' ')}</p>}

        {error && <p className="alert" role="alert">{error}</p>}

        <button type="submit" className="primary" disabled={submitting || url.trim() === ''}>
          {submitting ? 'Starting…' : 'Start crawl'}
        </button>
      </form>
    </section>
  );
}
