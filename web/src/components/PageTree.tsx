import { useState } from 'react';
import type { PageNode } from '../api';
import { displayUrl, isSameHost } from '../format';
import { StatusBadge } from './StatusBadge';

/**
 * The crawl result as a collapsible tree. A page's children are the pages first discovered on it.
 * Expanded/collapsed state lives here (a set of page ids) so "expand all" / "collapse all" can reset it.
 */
export function PageTree({ root }: { root: PageNode }) {
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set([root.id]));
  const rootUrl = new URL(root.url);

  const toggle = (id: number) =>
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  return (
    <div className="tree">
      <div className="tree-actions">
        <button className="link-button" onClick={() => setExpanded(new Set(allIds(root)))}>Expand all</button>
        <button className="link-button" onClick={() => setExpanded(new Set())}>Collapse all</button>
      </div>
      <div className="tree-row tree-header" aria-hidden="true">
        <span>Page</span>
        <span>Status</span>
        <span title="Links to the same host ÷ all links on the page">Domain Link Ratio ⓘ</span>
        <span>Links</span>
      </div>
      <ul className="tree-list">
        <TreeNode node={root} level={0} expanded={expanded} onToggle={toggle} host={rootUrl.host} hostname={rootUrl.hostname} />
      </ul>
    </div>
  );
}

interface TreeNodeProps {
  node: PageNode;
  level: number;
  expanded: Set<number>;
  onToggle: (id: number) => void;
  host: string;
  hostname: string;
}

function TreeNode({ node, level, expanded, onToggle, host, hostname }: TreeNodeProps) {
  const [showLinks, setShowLinks] = useState(false);
  const hasChildren = node.children.length > 0;
  const isOpen = expanded.has(node.id);

  return (
    <li>
      {/* Columns: page (indented by tree level) | status | ratio | links */}
      <div className="tree-row">
        <span className="tree-page" style={{ paddingLeft: `${level * 1.4}rem` }}>
        {hasChildren ? (
          <button
            className="toggle"
            onClick={() => onToggle(node.id)}
            aria-label={`${isOpen ? 'Collapse' : 'Expand'} ${displayUrl(node.url, host)}`}
            aria-expanded={isOpen}
          >
            {isOpen ? '▾' : '▸'}
          </button>
        ) : (
          <span className="toggle" aria-hidden="true" />
        )}

        <a className="tree-url" href={node.url} target="_blank" rel="noreferrer" title={node.url}>
          {displayUrl(node.url, host)}
        </a>
        {hasChildren && !isOpen && (
          <span className="muted child-count">+{node.children.length} page{node.children.length === 1 ? '' : 's'}</span>
        )}
        </span>

        <span><StatusBadge status={node.status} /></span>

        {node.error ? (
          <span className={`tree-error ${node.status === 'Failed' ? '' : 'muted'}`} title={node.error}>{node.error}</span>
        ) : (
          <>
            <span>{node.domainLinkRatio !== null && <RatioBar ratio={node.domainLinkRatio} />}</span>
            <span>
              {node.status === 'Crawled' && (
                <button className="link-button" onClick={() => setShowLinks(s => !s)} aria-expanded={showLinks}>
                  {node.outgoingLinks.length} {showLinks ? '▴' : '▾'}
                </button>
              )}
            </span>
          </>
        )}
      </div>

      {showLinks && (
        <ul className="link-list">
          {node.outgoingLinks.length === 0 && <li className="muted">No links on this page</li>}
          {node.outgoingLinks.map(link => {
            const internal = isSameHost(link, hostname);
            return (
              <li key={link}>
                <span className={`tag ${internal ? 'tag-internal' : 'tag-external'}`}>{internal ? 'internal' : 'external'}</span>
                <a href={link} target="_blank" rel="noreferrer">{link}</a>
              </li>
            );
          })}
        </ul>
      )}

      {hasChildren && isOpen && (
        <ul className="tree-list">
          {node.children.map(child => (
            <TreeNode key={child.id} node={child} level={level + 1} expanded={expanded} onToggle={onToggle} host={host} hostname={hostname} />
          ))}
        </ul>
      )}
    </li>
  );
}

function RatioBar({ ratio }: { ratio: number }) {
  const percent = Math.round(ratio * 100);
  return (
    <span className="ratio" title={`Domain Link Ratio: ${percent}% of links on this page point to the same host`}>
      <span className="ratio-track">
        <span className="ratio-fill" style={{ width: `${percent}%` }} />
      </span>
      {percent}%
    </span>
  );
}

function allIds(node: PageNode): number[] {
  return [node.id, ...node.children.flatMap(allIds)];
}
