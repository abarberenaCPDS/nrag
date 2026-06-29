What’s left is mostly **runtime/fixture parity**, not core .NET implementation.

**P0 / Blocked**
- **Agentic RAG exact parity**
  - .NET-native Agentic is implemented and gated behind `ENABLE_AGENTIC_RAG=true`.
  - Remaining: prove exact Python graph transitions, event timing, citations, follow-up behavior, and aggregate metrics against richer live Python fixtures.
  - Blocker: needs stronger Python/.NET Agentic runtime fixture baseline.

- **VLM/image citation asset parity**
  - Backend resolves bridge-provided visual asset metadata and Blazor renders visual citations.
  - Remaining: native NV-Ingest-created image/table/chart assets, Python thumbnail IDs, live object-store visual fixture comparison.
  - Blocker: NV-Ingest/NRL runtime or fixture bridge that creates real visual assets.

- **Full Python upload baseline**
  - Current blocker is still NV-Ingest unavailable on `localhost:7670`.
  - Preflight passed Python ingestor, Redis, object store, vector store, and mock embedding endpoint; failed only NV-Ingest.

**P1**
- **True streaming parity**
  - Direct SSE path works; stream usage is included when provider emits it.
  - Remaining: guarded/reflection/think-token paths still buffer; Python aggregate usage rollups and exact metrics are richer.

- **Query decomposition exact parity**
  - Main behavior is implemented.
  - Remaining: Python final response/citation generator differences and aggregate usage scopes.

- **Milvus / Chroma compatibility**
  - Interface-driven Chroma and Milvus support is in place.
  - Remaining: Milvus delete/update/compaction parity, full metadata grammar only when needed by supported DBs, live Milvus management fixtures with Python/NV-Ingest system collections.

- **Summary status parity**
  - .NET summaries use provider interfaces and object store.
  - Remaining: Redis cross-service persistence / parallel coordination semantics.

**P2 / Deferred**
- **VLM reranker**
  - Text reranking exists.
  - Remaining: multimodal image-passage reranker payloads, best deferred until visual citation assets are live.

- **Observability**
  - .NET has spans/metrics for major paths.
  - Remaining: exact Python metric families, span filtering details, aggregate usage rollups.

- **Health contract**
  - .NET health works.
  - Remaining: exact dependency-detail shape per deployment mode.

**Bottom line:** the main code gaps are now narrow. The big remaining items require live NV-Ingest/NRL or richer Python runtime baselines before we can honestly call them complete.

## What is missing?

For items **1-3**, this is what still needs to happen.

**1. Agentic RAG Exact Parity**
- Run live Python Agentic RAG with real services, not only deterministic mocks.
- Capture Python’s exact Agentic event flow:
  - planner stages
  - scope discovery rounds
  - task execution stages
  - verification/follow-up stages
  - final synthesis and citation payload
- Compare those events against the .NET-native Agentic workflow.
- Adjust .NET only where behavior differs, while keeping the implementation .NET-native and interface-based.
- Add fixture assertions for:
  - stage order
  - event payload shape
  - streamed chunks
  - citations
  - metric names/tags/rollups

Main blocker: we need a live Python Agentic baseline. The current mock parity is already passing.

**2. VLM / Image Citation Asset Parity**
- Get a fixture that produces real visual artifacts:
  - image chunks
  - table/chart assets
  - thumbnails
  - object-store paths/IDs
- Preferably produce these through Python NV-Ingest/NRL so .NET can match the real Python metadata shape.
- Validate that .NET resolves those assets through `ICitationAssetResolver`.
- Ensure final .NET citations match Python’s visual citation payload closely enough:
  - document type
  - page
  - source location
  - thumbnail/object-store identifiers
  - base64 or retrievable asset content
- Tighten VLM prompt/citation instruction wording if Python fixture shows a mismatch.
- Add Blazor regression coverage if any new citation fields need UI rendering.

Main blocker: NV-Ingest/NRL visual asset generation is not locally available yet.

**3. Full Python Upload Baseline**
- Start a real NV-Ingest runtime reachable at `localhost:7670`.
- Re-run:

```bash
uv run python fixtures/run_python_full_baseline.py \
  --base-url http://127.0.0.1:18097 \
  --run-fixtures \
  --out /tmp/python-full-baseline.json
```

- Use the resulting Python behavior as the baseline for:
  - upload task states
  - document metadata
  - object-store artifacts
  - Milvus system collections
  - delete/update/compaction behavior
  - summaries
  - visual asset output
- Compare .NET fixture output against that baseline.
- Implement only the gaps that are confirmed by the baseline.

Main blocker: `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0` needs NVCR/NGC access or a pre-pulled image.

For **4-8**, the work is mostly follow-on parity once the live Python/NV-Ingest baseline exists.

**4. Milvus Management Live Parity**
- Run Python upload/delete/update flows against real NV-Ingest + Milvus.
- Capture how Python populates and mutates:
  - normal document collections
  - `metadata_schema` system collection
  - `document_info` system collection
  - object-store summary/citation artifacts
- Compare .NET Milvus behavior for:
  - document listing
  - partial delete reporting
  - update/re-ingest behavior
  - compaction calls
  - cleanup of summary and citation assets
- Implement any confirmed gaps inside the Milvus provider or ingestor abstractions, not in controller-specific code.

Current state: local .NET Milvus management slices are done. Remaining work needs a live Python/NV-Ingest Milvus baseline.

**5. Query Decomposition Exact Runtime Parity**
- Compare live Python query decomposition SSE and non-SSE behavior against .NET.
- Verify:
  - query rewrite prompts
  - follow-up detection
  - per-subquery RAG prompt
  - final response prompt
  - citation merging
  - token usage spans
  - final answer shape
- Adjust .NET orchestration only where live Python shows differences.

Current state: deterministic mock fixtures already pass for multi-query and single-query paths. No obvious local slice remains unless a live baseline exposes a gap.

**6. Filter Compatibility**
- Keep filtering provider-owned:
  - Chroma translates supported filter shapes into Chroma-compatible syntax.
  - Milvus translates generated filter expressions into Milvus-compatible syntax.
- Add parser support only for confirmed missing filter shapes.
- Test both providers independently through `IVectorStoreFilterCapabilities` / vector-store abstractions.
- Avoid pushing Chroma/Milvus-specific filter logic into RAG orchestration.

Current state: Chroma supports nested `AND`/`OR`, ranges, equality, inequality, `IN`, `NOT IN`, booleans/numbers, and Python-style `source["field"]` selectors. Milvus owns generated-filter support.

**7. Summary Status / Redis Coordination**
- Decide whether .NET needs Redis-native summary state sharing for multi-instance deployments.
- If yes, add a Redis-backed summary progress/status provider behind the existing abstraction.
- Verify:
  - status survives process restart
  - multiple server instances see the same summary state
  - status transitions match Python where relevant
- Keep file/in-memory behavior for local/dev mode.

Current state: local summary abstractions exist. Redis parity is only needed if cross-service persistence becomes a deployment requirement.

**8. VLM Reranker Multimodal Payloads**
- Wait for real visual asset fixtures first.
- Once available, compare Python reranker payloads for multimodal chunks.
- Extend .NET reranker client/interface only if Python sends image/table/chart-aware reranking payloads.
- Add tests for text-only and visual citation cases so the interface remains provider-neutral.

Current state: intentionally deferred because VLM asset fixtures are the prerequisite.