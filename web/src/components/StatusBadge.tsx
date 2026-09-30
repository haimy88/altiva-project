import type { JobStatus, PageStatus } from '../api';

export function StatusBadge({ status }: { status: JobStatus | PageStatus }) {
  return <span className={`badge badge-${status.toLowerCase()}`}>{status}</span>;
}
