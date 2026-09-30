-- Crawl jobs: one row per crawl request.
CREATE TABLE crawl_jobs (
    id              uuid        PRIMARY KEY,
    start_url       text        NOT NULL,
    start_host      text        NOT NULL,              -- "starting domain" for the Domain Link Ratio
    max_depth       int         NOT NULL CHECK (max_depth BETWEEN 0 AND 5),
    max_pages       int         NOT NULL CHECK (max_pages BETWEEN 1 AND 1000),
    status          text        NOT NULL DEFAULT 'Pending'
                                CHECK (status IN ('Pending', 'Running', 'Completed', 'Failed', 'Canceled')),
    failure_reason  text        NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    started_at      timestamptz NULL,
    completed_at    timestamptz NULL
);

-- History screen: newest first, paginated. id breaks ties so paging is stable.
CREATE INDEX ix_crawl_jobs_created_at ON crawl_jobs (created_at DESC, id DESC);


-- Pages: every same-domain page the crawler has queued or crawled for a job.
-- parent_page_id = the page where this URL was FIRST discovered -> this is the tree.
CREATE TABLE pages (
    id                  bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    job_id              uuid        NOT NULL REFERENCES crawl_jobs (id) ON DELETE CASCADE,
    parent_page_id      bigint      NULL REFERENCES pages (id) ON DELETE CASCADE,
    url                 text        NOT NULL,          -- normalized absolute URL
    depth               int         NOT NULL CHECK (depth >= 0),
    status              text        NOT NULL DEFAULT 'Pending'
                                    CHECK (status IN ('Pending', 'Crawled', 'Failed', 'Skipped')),
    http_status         int         NULL,
    domain_link_ratio   double precision NULL CHECK (domain_link_ratio BETWEEN 0 AND 1),
    error               text        NULL,
    discovered_at       timestamptz NOT NULL DEFAULT now(),
    crawled_at          timestamptz NULL,

    -- Idempotency + "never process the same URL twice in a job".
    CONSTRAINT uq_pages_job_url UNIQUE (job_id, url)
);

-- Worker picks the next page to crawl (breadth-first). Partial index: only Pending rows,
-- so it stays tiny no matter how many pages are already crawled.
CREATE INDEX ix_pages_next_pending ON pages (job_id, depth, id) WHERE status = 'Pending';

CREATE INDEX ix_pages_parent ON pages (parent_page_id);


-- Edges: every outgoing link found on a crawled page (parent -> child URL).
-- Child is a URL, not a page id, because external links never become pages.
CREATE TABLE page_links (
    page_id      bigint  NOT NULL REFERENCES pages (id) ON DELETE CASCADE,
    target_url   text    NOT NULL,

    -- Idempotency: the same link on the same page is stored once.
    PRIMARY KEY (page_id, target_url)
);
