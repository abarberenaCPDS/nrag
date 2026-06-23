# Parity Dashboard

Last updated: `2026-06-21` (Python baseline run completed)

## Summary

| Domain | Total | Pass | Fail | Partial | Not Run |
|---|---:|---:|---:|---:|---:|
| RAG | 5 | 1 | 4 | 0 | 0 |
| Ingestor | 4 | 0 | 4 | 0 | 0 |
| Failure | 2 | 0 | 2 | 0 | 0 |
| Operability | 1 | 0 | 0 | 1 | 0 |
| **Overall** | 12 | 1 | 10 | 1 | 0 |

Execution note: `.NET` fixture run completed on June 21, 2026. Python fixture baseline also executed on June 21, 2026.

## Detailed Results

| Test Case ID | Domain | Python Result | .NET Result | Diff Summary | Status | Owner |
|---|---|---|---|---|---|---|
| RAG-HEALTH-001 | rag | fail | pass | Python `GET /health?check_dependencies=true` returned `500`; .NET returned `200` with expected health payload shape. | fail | |
| RAG-METRICS-001 | rag | fail | fail | Python returned metrics error text (`PROMETHEUS_MULTIPROC_DIR` not set); .NET returned scaffold placeholder metrics text. | fail | |
| RAG-GEN-001 | rag | fail | pass | Python `/generate` stream returned HTTP `400`; .NET returned `200` SSE with `finish_reason=stop` and `citations`. | fail | |
| RAG-SRCH-001 | rag | fail | pass | Python returned `400` (`Invalid vector store name: chroma`); .NET returned `200` search payload. | fail | |
| RAG-SUM-001 | rag | pass | pass | Python returned allowed `404` summary-not-found payload with required fields; .NET returned `202` pending payload. | pass | |
| ING-COL-001 | ingestor | fail | pass | Python returned `500` (`Invalid vector store name: chroma`); .NET returned `200` with expected collection metadata. | fail | |
| ING-DOC-001 | ingestor | fail | pass | Python blocking upload returned `500` (`Invalid vector store name: chroma`); .NET returned successful blocking upload response. | fail | |
| ING-DOC-002 | ingestor | fail | pass | Python non-blocking upload returned `500` and no `task_id`; .NET returned `200` with task enqueue response. | fail | |
| ING-STS-001 | ingestor | fail | pass | Python status check could not execute (`task_id` missing from failed ING-DOC-002); .NET returned `200` with status schema. | fail | |
| FAIL-RERANK-001 | failure | fail | pass | Python returned `400` (`Invalid vector store name: chroma`) before fallback behavior could be assessed; .NET returned graceful fallback `200`. | fail | |
| FAIL-VDB-001 | failure | fail | partial | Python returned `400` (`Invalid vector store name: chroma`) instead of expected backend-unavailable `5xx`; .NET remained partial due no deterministic outage injection. | fail | |
| OPS-TRACE-001 | operability | partial | partial | Python request returned `200` SSE but trace-export/span assertions were not executed; same gap remains in .NET run. | partial | |

## Diff Legend

- `pass`: behavior equivalent
- `partial`: non-breaking differences remain
- `fail`: parity broken
- `not_run`: not executed yet

## Blocking Gaps

- `RAG-METRICS-001`: .NET metrics endpoint is currently scaffold output.
- `FAIL-VDB-001`: Failure fixture needs explicit automation to force vector-store unavailability.
- `OPS-TRACE-001`: Trace emission/export verification is still pending.
- Python baseline currently fails most RAG/Ingestor fixtures with `Invalid vector store name: chroma` in this local runtime configuration.
