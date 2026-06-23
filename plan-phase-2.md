# Phase 2 Plan: .NET Ingestion Worker Service

1. Create `dotnet-ingestion-worker-service` project.
2. Define shared ingestion job contracts in `utils` (enqueue request, job payload, status/update events).
3. Add a queue abstraction (`IIngestionQueue`) with an in-memory implementation first, Redis-backed option second.
4. Move heavy ingestion pipeline from `dotnet-ingestor-server` request path into worker handlers (parse/chunk/embed/upsert/summarize).
5. Keep `dotnet-ingestor-server` as control plane:
   - `POST /documents` validates input and enqueues jobs.
   - `GET /status` reads job state from shared store.
6. Add shared job-state store abstraction (`IIngestionJobStore`) with durable backend option (Redis/Postgres).
7. Update compose to run 4 app services: `rag`, `ingestor`, `reranker`, `ingestion-worker`.
8. Add env config per service (`APP_INGEST_QUEUE_*`, worker concurrency, retry/backoff, visibility timeout).
9. Add failure semantics:
   - retries with max attempts,
   - dead-letter handling,
   - idempotency key per file/job.
10. Add observability:
   - queue depth,
   - job latency,
   - success/failure counters,
   - worker health and readiness endpoints.
11. Add tests:
   - unit tests for queue/job orchestration,
   - integration tests for enqueue→worker→status flow,
   - failure tests for worker down, retry exhaustion, and duplicate submissions.
12. Rollout strategy:
   - feature flag `INGEST_ASYNC_ENABLED`,
   - dual mode (sync legacy path + async worker path),
   - switch default to async after validation.
