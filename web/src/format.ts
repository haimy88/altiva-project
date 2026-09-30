export const formatTime = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '—');

/** "1m 23s" between two timestamps (end defaults to now, for jobs still running). */
export function formatDuration(startIso: string | null, endIso: string | null): string {
  if (!startIso) return '—';
  const ms = (endIso ? new Date(endIso) : new Date()).getTime() - new Date(startIso).getTime();
  const seconds = Math.max(0, Math.round(ms / 1000));
  const m = Math.floor(seconds / 60);
  return m > 0 ? `${m}m ${seconds % 60}s` : `${seconds}s`;
}

/** Same-host URLs are shown as their path ("/blog/"), others in full. */
export function displayUrl(url: string, host: string): string {
  try {
    const u = new URL(url);
    return u.host.toLowerCase() === host.toLowerCase() ? u.pathname + u.search : url;
  } catch {
    return url;
  }
}

/** Same rule as the backend (DomainLinkRatio.IsSameDomain): exact host, case-insensitive. */
export function isSameHost(url: string, host: string): boolean {
  try {
    return new URL(url).hostname.toLowerCase() === host.toLowerCase();
  } catch {
    return false;
  }
}
