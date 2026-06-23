# Fixtures Baseline

This directory stores Phase 0 parity fixtures used to compare Python and .NET behavior.

## Layout

- `test-case-catalog.csv`: Canonical test-case IDs and mapping metadata.
- `parity-dashboard.md`: Snapshot of pass/fail parity outcomes.
- `rag/`: Request/expected payloads for RAG endpoints.
- `ingestor/`: Request/expected payloads for ingestion endpoints.
- `failure/`: Fixtures for failure-injection scenarios.
- `operability/`: Fixtures for health/metrics/tracing checks.

## Naming convention

- Request payloads: `requests/<TEST_CASE_ID>.json`
- Expected payloads: `expected/<TEST_CASE_ID>.json`
- Supplemental notes: `expected/<TEST_CASE_ID>.md`

Example:
- `rag/requests/RAG-GEN-001.json`
- `rag/expected/RAG-GEN-001.json`

## Rules

1. Do not overwrite old fixtures; add a new ID when behavior intentionally changes.
2. Keep payloads minimal but complete enough to reproduce behavior.
3. Store secrets/tokens as placeholders only.
4. Always reference the test-case ID from `test-case-catalog.csv`.
