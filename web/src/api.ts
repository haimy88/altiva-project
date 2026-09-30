// Typed client for the Crawl API. Shapes mirror src/Crawler.Api/Jobs/JobContracts.cs (camelCase JSON).

export type JobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Canceled';
export type PageStatus = 'Pending' | 'Crawled' | 'Failed' | 'Skipped';

export const isFinal = (s: JobStatus) => s === 'Completed' || s === 'Failed' || s === 'Canceled';

export interface JobProgress {
  pagesDiscovered: number;
  pagesCrawled: number;
  pagesFailed: number;
  pagesSkipped: number;
  pagesPending: number;
}

export interface JobSummary {
  jobId: string;
  url: string;
  status: JobStatus;
  maxDepth: number;
  maxPages: number;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failureReason: string | null;
  progress: JobProgress;
}

export interface JobListItem {
  jobId: string;
  url: string;
  status: JobStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface PageNode {
  id: number;
  url: string;
  depth: number;
  status: PageStatus;
  httpStatus: number | null;
  domainLinkRatio: number | null;
  error: string | null;
  outgoingLinks: string[];
  children: PageNode[];
}

export interface JobTree {
  jobId: string;
  status: JobStatus;
  root: PageNode | null;
}

/** An API error, built from the ProblemDetails body. fieldErrors is set for 400 validation errors. */
export class ApiError extends Error {
  constructor(
    message: string,
    public readonly status: number,
    public readonly fieldErrors: Record<string, string[]> = {},
  ) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      ...init,
      headers: { 'Content-Type': 'application/json', ...init?.headers },
    });
  } catch (e) {
    if (init?.signal?.aborted) throw e; // we canceled it ourselves (e.g. left the page): not an error to show
    throw new ApiError('Cannot reach the API. Is it running?', 0);
  }

  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new ApiError(
      problem?.detail ?? problem?.title ?? `Request failed (${response.status})`,
      response.status,
      problem?.errors ?? {},
    );
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  createJob: (url: string, maxDepth?: number) =>
    request<{ jobId: string }>('/api/jobs', { method: 'POST', body: JSON.stringify({ url, maxDepth }) }),
  getJob: (jobId: string, signal?: AbortSignal) => request<JobSummary>(`/api/jobs/${jobId}`, { signal }),
  getTree: (jobId: string, signal?: AbortSignal) => request<JobTree>(`/api/jobs/${jobId}/tree`, { signal }),
  listJobs: (page: number, pageSize: number) => request<Paged<JobListItem>>(`/api/jobs?page=${page}&pageSize=${pageSize}`),
  cancelJob: (jobId: string) => request<void>(`/api/jobs/${jobId}/cancel`, { method: 'POST' }),
};
