# Phase 0: Parity Matrix and Golden Fixture Checklist

## Goal
Define the source-of-truth parity baseline between Python (`src/nvidia_rag`) and .NET (`src/dotnet_rag`) before implementation phases.

## Endpoint Parity Matrix

| Area | Endpoint | Python | .NET | Parity Status | Notes |
|---|---|---|---|---|---|
| RAG | `GET /health`, `/v1/health` | Yes | Yes | Partial | .NET dependency checks are stubbed. |
| RAG | `GET /configuration`, `/v1/configuration` | Yes | Yes | Partial | Defaults exist; behavior-level validation pending. |
| RAG | `GET /metrics`, `/v1/metrics` | Yes | Yes | Gap | .NET returns scaffold placeholder text. |
| RAG | `POST /generate`, `/v1/generate` | Yes | Yes | Partial | Core path exists; advanced behaviors missing. |
| RAG | `POST /chat/completions`, `/v1/chat/completions` | Yes | Yes | Partial | Basic compatibility exists. |
| RAG | `POST /search`, `/v1/search` | Yes | Yes | Partial | Filter/query-rewrite/reflection parity missing. |
| RAG | `POST /v2/vector_stores/{id}/search` | Yes | Yes | Partial | .NET contract exists; filters/ranking semantics incomplete. |
| RAG | `GET /summary`, `/v1/summary` | Yes | Yes | Gap | .NET timeout/blocking semantics not equivalent. |
| Ingestor | `GET /health`, `/v1/health` | Yes | Yes | Partial | .NET dependency checks are minimal. |
| Ingestor | `POST /documents` | Yes | Yes | Gap | Non-blocking mode is not truly async in .NET. |
| Ingestor | `PATCH /documents` | Yes | Yes | Partial | Update path exists; parity behavior tests needed. |
| Ingestor | `GET /status` | Yes | Yes | Partial | In-memory status only; no durable queue/store yet. |
| Ingestor | `GET /documents` | Yes | Yes | Partial | Metadata/detail parity gaps. |
| Ingestor | `DELETE /documents` | Yes | Yes | Partial | Basic path exists; full metadata/accounting parity pending. |
| Ingestor | `GET /collections` | Yes | Yes | Partial | Backing behavior differs by store backend support. |
| Ingestor | `POST /collections` | Yes (deprecated) | Yes | Partial | Keep for compatibility; parity tests needed. |
| Ingestor | `POST /collection` | Yes | Yes | Partial | Metadata schema validation parity missing. |
| Ingestor | `PATCH /collections/{collection}/metadata` | Yes | Yes | Partial | Basic update exists; full validation parity pending. |
| Ingestor | `PATCH /collections/{collection}/documents/{document}/metadata` | Yes | Yes | Partial | Basic update exists; schema enforcement missing. |
| Ingestor | `DELETE /collections` | Yes | Yes | Partial | Basic path exists; backend parity pending. |
| Internal | `POST /v1/rerank` | Python in-proc/provider path | Yes (service) | Partial | Service exists; full scenario tests required. |

## Capability Parity Matrix

| Capability | Python | .NET | Status |
|---|---|---|---|
| Reranker service abstraction + fallback | Yes | Yes | Partial |
| Async ingestion worker + queue + durable state | Yes (task/background model) | Not yet | Gap |
| Agentic RAG | Yes | Config flag only | Gap |
| Query rewriting | Yes | Not implemented end-to-end | Gap |
| Filter expression generation/validation | Yes | Not implemented end-to-end | Gap |
| Reflection loop | Yes | Not implemented | Gap |
| Guardrails integration | Yes | Not implemented end-to-end | Gap |
| VLM routing + multimodal query behavior | Yes | Not implemented end-to-end | Gap |
| Metadata schema validation/system fields | Yes | Limited | Gap |
| NV-Ingest/NRL ingestion path | Yes | Not equivalent | Gap |
| Object-store-backed citation assets | Yes | Not equivalent | Gap |
| Multi-vector-backend parity (ES/Milvus/LanceDB) | Yes | Chroma-first | Gap |
| Real observability (metrics/traces/health deps) | Yes | Partial/scaffold | Gap |
| Automated parity test suite | Yes (Python tests) | Not established | Gap |

## Golden Fixture Checklist

## RAG fixtures
- [ ] `generate` text-only, KB on, reranker on.
- [ ] `generate` text-only, KB off.
- [ ] `generate` with `enable_query_rewriting=true`.
- [ ] `generate` with `enable_filter_generator=true`.
- [ ] `generate` with reflection enabled.
- [ ] `generate` with guardrails enabled.
- [ ] `generate` agentic request (`agentic=true`).
- [ ] `generate` multimodal payload with image content.
- [ ] `chat/completions` non-streaming OpenAI-compatible payload.
- [ ] `search` text query baseline.
- [ ] `search` with explicit `filter_expr`.
- [ ] `search` with confidence threshold and reranker toggles.
- [ ] `v2/vector_stores/{id}/search` with OpenAI filter + ranking options.
- [ ] `summary` immediate available case.
- [ ] `summary` pending case with `blocking=false`.
- [ ] `summary` timeout path with `blocking=true`.

## Ingestor fixtures
- [ ] `POST /collection` with metadata schema and catalog fields.
- [ ] `POST /documents` blocking upload success.
- [ ] `POST /documents` non-blocking enqueue + status progression.
- [ ] `PATCH /documents` update existing file.
- [ ] Duplicate filename handling in single upload.
- [ ] Unsupported file type handling.
- [ ] Summary generation with strategy + page filter.
- [ ] `GET /documents` large response with metadata expectations.
- [ ] `DELETE /documents` partial and full deletion.
- [ ] `PATCH` collection metadata update.
- [ ] `PATCH` document metadata update.
- [ ] `DELETE /collections` success + not-found handling.

## Failure fixtures
- [ ] Reranker service down.
- [ ] Reranker fallback provider down.
- [ ] Vector store unavailable.
- [ ] LLM endpoint unavailable.
- [ ] Worker down with queued jobs.
- [ ] Retry exhaustion to dead-letter path.
- [ ] Duplicate ingestion submission idempotency.

## Operability fixtures
- [ ] Health with `check_dependencies=true` validates downstream checks.
- [ ] Metrics endpoint exposes real counters/histograms (no placeholder).
- [ ] Trace spans emitted for request and downstream calls.

## Execution Readiness Checklist
1. [ ] Convert each checklist item into an automated test case ID.
2. [ ] Store canonical request/response payloads under a `fixtures/` directory.
3. [ ] Add pass/fail parity dashboard doc (`python_result`, `dotnet_result`, `diff`).
4. [ ] Gate Phase 2+ merges on fixture regression results.
