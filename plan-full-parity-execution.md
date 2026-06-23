# .NET Full-Parity Migration Plan (Execution Ready)

## Objective
Achieve functional parity between `src/nvidia_rag` (Python) and `src/dotnet_rag` (.NET) with staged rollout and production-safe fallback.

## Scope
- In scope: `rag_server`, `ingestor_server`, `reranker_service`, shared `utils`, compose/runtime wiring, tests, observability.
- Out of scope: Python feature redesign, breaking external API changes.

## Guiding Principles
1. Keep external API contracts stable while replacing internals.
2. Isolate volatile capabilities into dedicated services.
3. Ship behind feature flags, validate, then switch defaults.
4. No parity claim without automated tests and measurable SLOs.

## Phase 0: Execution Readiness (Kickoff)
1. Freeze parity baseline:
   - Capture Python endpoint/behavior matrix (`health`, `generate`, `search`, `summary`, ingestion CRUD/status).
   - Record canonical request/response samples for regression tests.
2. Define parity gates:
   - Contract parity gate, behavior parity gate, perf gate, operability gate.
3. Create work tracking:
   - Epic per phase, story per endpoint/feature, explicit acceptance criteria.
4. Create .NET test projects:
   - Unit + integration test projects added to `DotnetRag.sln`.

Exit criteria:
- Baseline matrix and golden fixtures committed.
- Test projects scaffolded and executing in CI/local.

## Phase 1: Reranker Service (Completed/Validate)
1. Confirm reranker-service owns provider fallback behavior.
2. Verify rag-server integration path uses internal reranker API.
3. Re-run failure tests:
   - reranker unavailable
   - fallback provider unavailable
   - reranker disabled mode
4. Lock acceptance tests for rerank path.

Exit criteria:
- Phase 1 tests green and reproducible.

## Phase 2: Async Ingestion Worker Service (Next Build)
1. Create `dotnet-ingestion-worker-service`.
2. Define shared ingestion job contracts in `utils` (enqueue request, job payload, status/update events).
3. Add `IIngestionQueue` (in-memory first, Redis-backed second).
4. Move heavy ingestion pipeline from request path to worker handlers (parse/chunk/embed/upsert/summarize).
5. Keep `dotnet-ingestor-server` as control plane:
   - `POST /documents` validates and enqueues.
   - `GET /status` reads job state from shared store.
6. Add shared `IIngestionJobStore` with durable backend option (Redis/Postgres).
7. Update compose to run 4 app services: `rag`, `ingestor`, `reranker`, `ingestion-worker`.
8. Add env config (`APP_INGEST_QUEUE_*`, concurrency, retry/backoff, visibility timeout).
9. Add failure semantics (retry max, dead-letter, idempotency key).
10. Add observability (queue depth, job latency, success/failure counters, worker health/readiness).
11. Add tests (orchestration unit tests, enqueue->worker->status integration, failure tests).
12. Rollout with `INGEST_ASYNC_ENABLED` dual mode, then switch default after validation.

Exit criteria:
- Non-blocking ingestion returns immediately and status progresses asynchronously.
- Retry/dead-letter/idempotency paths verified by tests.

## Phase 3: RAG Intelligence Parity
1. Implement query rewriting parity.
2. Implement filter-expression generation + validation parity.
3. Implement reflection loop parity.
4. Implement guardrails parity (request-level toggle + global toggle behavior).
5. Implement VLM routing parity (image-aware behavior and fallback rules).
6. Implement Agentic RAG path parity (enable flag + request-level override).

Exit criteria:
- `generate` and `search` behavior matches Python for toggles and edge cases.
- Golden test fixtures pass for all toggled combinations.

## Phase 4: Ingestion/Metadata/Backend Parity
1. Implement metadata schema validation and system-managed metadata behavior.
2. Implement multimodal ingestion parity path (NV-Ingest/NRL equivalent strategy or explicit compatibility adapter).
3. Implement object-store citation asset parity.
4. Expand vector DB backend support beyond Chroma (Elasticsearch/Milvus/LanceDB strategy).
5. Align summary retrieval semantics (`blocking` + timeout behavior).

Exit criteria:
- Ingested metadata/filter semantics match Python.
- Multimodal and summary behavior validated end-to-end.

## Phase 5: Operability, Performance, and Cutover
1. Replace scaffold metrics with real metrics + tracing.
2. Implement real dependency health checks.
3. Add load/perf tests for generate/search/ingestion under concurrency.
4. Execute shadow/dual-run validation against Python baseline.
5. Cutover by feature flags and retain rollback switches.

Exit criteria:
- SLOs met under target load.
- Rollback plan tested.
- Parity scorecard signed off.

## Cross-Cutting Test Strategy
1. Contract tests for all public endpoints.
2. Golden behavior tests from Python fixtures.
3. Failure-injection tests (downstream unavailability, timeout, malformed payloads).
4. Compatibility tests for OpenAI-style endpoints.

## Feature Flag Plan
- `INGEST_ASYNC_ENABLED`
- `ENABLE_AGENTIC_RAG`
- `ENABLE_QUERYREWRITER`
- `ENABLE_FILTER_GENERATOR`
- `ENABLE_GUARDRAILS`
- `ENABLE_VLM_INFERENCE`
- `ENABLE_RERANKER`

Rule:
- New behavior defaults off until parity tests pass, then switch default and monitor.

## Execution Order (Recommended)
1. Phase 0 (readiness)
2. Phase 2 (ingestion worker)
3. Phase 3 (RAG intelligence)
4. Phase 4 (ingestion/backend parity)
5. Phase 5 (operability and cutover)

## Immediate Next Actions
1. Finalize parity matrix and fixture set.
2. Scaffold `.NET` test projects and wire to solution.
3. Start Phase 2 implementation branch:
   - add worker service project
   - add queue/job-store abstractions
   - route non-blocking uploads to enqueue path
