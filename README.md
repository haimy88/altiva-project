# Crawler

## Goal
Crawl a site's internal links up to a given depth, present the tree of links to the user with corresponding Domain-Link ratio.

## How to run
1. Clone repo
2. Build images with the following command:
   ```bash
   docker compose up --build
   ```
3. Open:
   - http://localhost:8080 for the main app
   - http://localhost:5080/health for health check
   - http://localhost:15672 for RabbitMQ dashboard; login `crawler` / `crawler`

Testing with this command (needs the .NET 8 SDK and Docker; GitHub Actions runs this on every push):
```bash
dotnet test
```

## Development Process
Prioritized in the following order:
1. Backend queue and DB
2. Crawling logic
3. Retry logic
4. React UI

I wanted to make sure core backend logic worked well first as it is the engine driving the crawler app; it's most important to get right.

Worked on the project throughout the day, between breaks and research.

**Cut:**
- SignalR
- Outbox for coupled failing for job saved and job message posted

## Architecture
- Distributed backend
- **Services**
  - API Service
  - Worker Service
- **Shared code libraries**
  - Infrastructure Library
  - Domain Library
- **Tests**
  - Unit tests for normalization, ratio and retry decisions
  - End-to-end crawl tests with HTML
- **One database**
  - Services share a database for persistence
  - Considered having only API write to database but introduces more points of failure
  - Tables
    - `crawl_jobs` – status and timestamps
    - `pages` – page and parent, serves as crawl queue
    - `page_links` – all outgoing links
- **Message Broker**
  - RabbitMQ passes messages between services
- **UI:** React with Vite

## Assumptions
- The same URL submitted twice should produce two separate jobs since each is a snapshot and the site may have changed
- Job capped at 200 pages, max depth allowed is 5
- The prefix "www" is not counted as the same site
- The traffic will be relatively low
- Pages are plain HTML only (ignores links from JS)

## Technologies Chosen

### Backend Services – .NET 8
- Required by assignment

### Database – PostgreSQL
- Also considered SQL Server since it is a first class Microsoft service as well, and personally familiar
- PostgreSQL has better portability and easier for me to develop locally; Apple Silicon M1 would need to use x64 emulation for SQL Server
- PostgreSQL is lighter than SQL Server, taking up 29MB of RAM instead of 2GB
- Fast and native on every platform

### Message Broker – RabbitMQ instead of Kafka
- RabbitMQ has native dead letter exchange
- Native building blocks for delayed retries
  - Message TTL
  - Delivery limits
  - Automatic moving rejected/expired messages to a separate queue
- Per-message acks for at-least-once delivery and idempotency
- Future proof – can easily scale up by adding consumers without need for partitioning
- Kafka's main advantage of high throughput isn't relevant since traffic is expected to be low

### React with Vite
- Higher quality of life for developers than Create React App

## Message Schema
```json
{ "messageId", "jobId", "correlationId", "requestedAt", "schemaVersion": 1 }
```
Intentionally kept thin since the DB is the source of truth.

## Idempotency
- RabbitMQ ensures at-least-once delivery with ack
- Unique keys + `ON CONFLICT DO NOTHING` ensures each link and page are only listed once per job
- Normalization of the URLs ensures duplications are caught if URLs have different spellings for the same page (unique key catches it)
- Workers will skip a duplicate of a completed job and resume the progress of a crashed job

## Retry Policy
### Pages
- 10s per attempt
- 2 retries with backoff and jitter
- Per-host circuit breaker
- Only network errors, 5xx, 408, 429 are retried
- Failed page doesn't fail the job

### Jobs
- If transient error, retry every 10s up to 5 attempts total. Send to dead letter queue if failed
- Otherwise send straight to dead letter queue

## Dead Letter Queue
- Unreadable messages, crash loops, out-of-retries, permanent errors
- Job marked failed and the message has error headers
- Replay message by republishing via RabbitMQ UI

## Observability
- Structured JSON with jobId and correlationId on every line
- Timestamp, logger level, category, message, scopes
- Key events are logged
- Health endpoints

## Trade-offs
- Sharing a database is sometimes considered an antipattern in distributed systems due to coupling of interests. However, separating the DB would have necessitated more logic and interservice communication, introducing complexity and points of failure.
- Chose polling over SignalR since it is simpler for this job, doesn't require open connections, and the brief allows it

## Known Limitations
- Job stays running if DB is down when worker gives up
- URL that redirects to another host is not processed
  - E.g., http://google.com will not be processed because it redirects to http://www.google.com
  - Brief defines domain as the exact host of the start URL, so technically the "www" is discriminating

## Next Steps with more time
- Try to go over the code more carefully and see if any other variable should be configurable in settings
- Make the tree update dynamically with new nodes instead of re-fetching the whole tree
- Add Outbox for a single transaction for a job being saved to DB and a job message posted so they fail and succeed together. This prevents a situation where a job is saved but doesn't get posted and remains pending.
- Add option for user to crawl a redirected website
- Consuming job messages has a fixed 10s retry, it's better if the time grows exponentially
- Stale job sweeper to deal with jobs that are stuck in running limbo
- Add OpenTelemetry for much more granular performance measuring
