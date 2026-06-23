# OPS-TRACE-001 Expected Outcome

- HTTP request succeeds (`200` with SSE response envelope).
- A server span is emitted for the request handler.
- At least one downstream span is emitted (LLM call and/or vector store call, depending on config).
- Trace contains a consistent correlation key (trace id) across related spans.

## Minimum assertions

1. Span count `>= 1` for service `rag_server`.
2. One span name matches request route handling (`/generate` equivalent).
3. Trace export contains no serialization/export errors.

## Notes

- This fixture is expected to fail until full tracing is implemented and verified in .NET.
