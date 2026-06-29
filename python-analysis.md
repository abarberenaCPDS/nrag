# Python Ingestor Parity Analysis

Date: 2026-06-26

Scope: source Python ingestor at `src/nvidia_rag/ingestor_server/main.py` and directly relevant dependencies, compared with the .NET target under `src/dotnet_rag/ingestor_server`.

## Context From Repo Notes

- Root planning docs (`plan-full-parity-execution.md`, `plan-phase-0-parity-matrix.md`, `plan-phase-2.md`) classify ingestion as not yet behaviorally equivalent. Known gaps include async ingestion worker/durable state, metadata schema/system fields, NV-Ingest/NRL ingestion behavior, object-store citation assets, multi-vector-backend parity, real dependency health, and summary semantics.
- `CLAUDE.md` documents the current .NET design decision: `InMemoryIngestorStore` is not a proxy to Python, collection/document metadata is in-memory, ChromaDB is shared only for vector search, and .NET requires a two-step workflow (`POST /collection` before `POST /documents`).
- `fixtures/parity-dashboard.md` contains useful fixture history, but many Python failures are due to local `chroma` config incompatibility. Treat those fixture results as runtime observations, not a replacement for source-code parity review.

## Python Source Behavior: `NvidiaRAGIngestor`

- Initialization supports `library`, `server`, and `lite` modes. Lite forces Milvus and disables object-store operations. LanceDB is explicitly allowed only with the NRL backend. The constructor configures object storage, initializes an NV-Ingest client, keeps a background summary task set alive, and lazily creates the NRL handler only when `config.nv_ingest.backend == "nrl"`.
- `SUPPORTED_FILE_TYPES` is derived from NV-Ingest's `EXTENSION_TO_DOCUMENT_TYPE` minus `svg`. Unsupported detection is therefore NV-Ingest-driven, not a small hard-coded extension list.
- `__prepare_vdb_op_and_collection_name` is central. It creates the configured VDB operator through `_get_vdb_op`, passes through per-request bearer auth (`vdb_auth_token`), custom metadata, file paths, and metadata schema, then returns the backend-canonicalized collection name. The comment explicitly calls out Elasticsearch lowercasing so summary/object-store keys match `GET /collections`.
- `upload_documents` auto-creates a missing collection with default settings before ingestion. This differs from the documented .NET two-step requirement.
- Non-blocking upload creates an `IngestionStateManager`, submits the real ingestion coroutine to `INGESTION_TASK_HANDLER`, stores initial `nv_ingest_status`, and writes an initial in-progress response. Blocking upload runs the same background task inline.
- Python validates `summary_options` with the FastAPI/Pydantic `SummaryOptions` model even in library mode. That includes page-filter shape validation and allowed summarization strategy values.
- Python validates metadata before ingest using the collection schema from VDB. Invalid metadata filters out only the failed files; valid metadata can be normalized, then the VDB operator is recreated so the normalized metadata CSV/config is used by ingestion.
- File validation includes path traversal detection via `Path.resolve(strict=True)`, existence checks, regular-file checks, duplicate-existing-document rejection for `POST`, and explicit `.rst`/`.rtf`/`.org` unsupported messaging with Pandoc install guidance.
- Actual ingestion routes through either the NRL path or NV-Ingest path. NRL is limited to LanceDB persistence; Milvus/Elasticsearch with NRL raises. NV-Ingest is the default path.
- The final response is built from NV-Ingest/NRL extraction results and VDB verification. It includes failed NV-Ingest documents, unsupported files, and documents missing from VDB after ingest. Successful documents include `metadata` and computed `document_info`.
- In server mode, uploaded temp files are cleaned up after ingestion completes.

## Dependency Behavior Worth Porting Or Explicitly Deferring

- `server.py` extracts `Authorization: Bearer <token>` and passes it to VDB operations for upload, update, list, delete, collection list, and collection delete.
- `server.py` deduplicates upload filenames before writing to `TEMP_DIR/uploaded_files/{collection_name}` and returns validation errors for discarded duplicates.
- `IngestionStateManager` tracks `documents_completed`, `batches_completed`, accumulated completed documents, and per-document NV-Ingest statuses (`not_started`, `completed`, etc.) under a lock.
- `task_handler.py` optionally persists task status and custom state dictionaries in Redis when `ENABLE_REDIS_BACKEND=True`; otherwise it uses process memory.
- `nvingest.py` builds the ingestion pipeline: file load, optional PDF split config, extraction, split, optional image captioning, embedding, optional save-to-disk, object-store `.store()` for citation assets, and VDB upload.
- Metadata schema support is richer than .NET currently models: Python supports `string`, `datetime`, `number`, `integer`, `float`, `boolean`, and typed `array`; reserved system fields (`type`, `subtype`, `location`) cannot be custom fields; RAG-managed fields like `filename` and auto-extracted fields like `page_number` are injected with `user_defined` and `support_dynamic_filtering` flags.
- Metadata validation normalizes datetime values to UTC `Z`, coerces booleans through a defined value map, enforces required fields, array element types, max lengths, and forbids unexpected fields through a generated Pydantic model.
- Document info is not just catalog text. Python computes `document_type`, `file_size`, `date_created`, `doc_type_counts`, `total_elements`, and `raw_text_elements_size`; collection info aggregates these into flags such as `has_tables`, `has_charts`, `has_images`, `number_of_files`, `last_indexed`, and ingestion status/error fields.

## Update/Delete Semantics In Python

- `PATCH /documents` first deletes existing documents, then re-ingests. In server mode it deletes by upload-path-relative name; in library mode it uses caller-provided file paths.
- If any rows were deleted, Python compacts Milvus before re-ingest. The comments explain this is required because stale `indexed_rows` can make NV-Ingest wait for an impossible expected count.
- During Milvus compaction Python snapshots uploaded files to hidden temp copies to guard against concurrent background cleanup deleting shared upload paths, then restores and removes snapshots in `finally`.
- `DELETE /documents` asks the VDB for detailed `deleted` vs `not_found` results, returns partial-delete messages, deletes object-store citation assets and summaries for successfully deleted docs, and recalculates collection metrics from remaining documents to avoid double aggregation.
- `DELETE /collections` delegates to the VDB backend and also removes collection citation assets plus summary objects from object storage when available.

## .NET Target Gaps Observed In Source

- Current .NET `POST /documents` requires the collection to already exist in `InMemoryIngestorStore`; Python auto-creates missing collections before ingestion.
- .NET accepts `vdb_endpoint` in contracts/routes but mostly ignores it in `IngestorService`; Python uses it to construct the active VDB operator per request. .NET also does not appear to pass bearer auth through to vector-store operations.
- .NET collection/document listing is backed by `InMemoryIngestorStore`, not the configured VDB metadata/document-info collections. This means process restart loses catalog state and Blazor/.NET collections remain separate from Python/React collections by design.
- .NET non-blocking ingestion uses a process-local fire-and-forget `Task.Run` status map. Python has an async task handler plus optional Redis state backend and updates per-document NV-Ingest status during ingestion polling.
- .NET ingestion extracts text locally (`PdfPig`, OpenXML, HTML regex, raw text), chunks, embeds, and upserts to Chroma/Milvus abstractions. It does not run the NV-Ingest pipeline, NRL pipeline, multimodal extraction, image captioning, object-store asset storage, save-to-disk results, or NV-Ingest VDB upload semantics.
- .NET unsupported formats are hard-coded to `.rst`, `.rtf`, `.org`, `.svg`; Python derives supported types from NV-Ingest and reports all unsupported extensions in final failure handling.
- .NET summary option validation only checks `summary_options` requires `generate_summary`; it does not enforce Python's page-filter shape rules or allowed strategy names at request parsing time.
- .NET metadata schema types are documented in `CLAUDE.md` as `str`, `int`, `float`, `bool`, while Python API schema uses `string`, `datetime`, `number`, `integer`, `float`, `boolean`, `array`. This is a contract mismatch for parity.
- .NET metadata validation is ad hoc and limited to required fields plus int/float/bool parsing. It does not support Python's datetime normalization, typed arrays, max lengths, reserved/system-managed fields, `user_defined`, or `support_dynamic_filtering`.
- .NET `CreateCollectionAsync` stores only user-provided schema/catalog fields and ensures a Chroma collection. Python injects RAG-managed metadata fields, creates metadata-schema and document-info collections, stores catalog metadata with `date_created`/`last_updated`, and uses backend duplicate checks.
- .NET document responses lack Python's generated `document_id`, `size_bytes`, and computed `document_info` metrics. It mostly returns upload path plus optional catalog description/tags.
- .NET delete/update paths do not recalculate collection metrics from remaining documents, remove object-store citation/summary assets, or perform Milvus compaction/concurrency temp-file protection.
- .NET health dependency checks are stubbed to healthy ingestor/task handler entries; Python can check VDB, object storage, NIM, processing, and task management dependencies.

## Recommended Parity Work Items For Ingestor

1. Decide whether .NET should preserve the current two-step collection requirement or match Python auto-create-on-upload. If preserving current behavior, mark it as an accepted divergence in the parity matrix.
2. Introduce a real ingestion job abstraction/store per `plan-phase-2.md`, including durable optional state, per-document status progression, retries, dead-letter/idempotency, and worker health metrics.
3. Align ingestor contracts with Python metadata schema vocabulary and validation behavior (`string`/`datetime`/typed arrays/system fields), or add explicit compatibility translation at the API boundary.
4. Replace or wrap local text extraction with a strategy that can emulate Python NV-Ingest/NRL output shape, including multimodal elements, object-store assets, document info aggregation, and failure reporting.
5. Make VDB endpoint/auth handling real in .NET routes and service methods, or remove exposed knobs until backend support exists.
6. Persist collection/document catalog and document-info state outside `InMemoryIngestorStore` if .NET is expected to survive process restarts or interoperate with Python/React-created collections.
7. Add focused fixtures for: auto-create vs require-create, metadata schema validation/normalization, duplicate uploads, unsupported extension reporting, partial deletes, update compaction semantics, summary option validation, and per-document status progression.

## .NET Implementation Slice Completed

- Added `IVectorStoreManagement` so ingestor collection/document lifecycle code can target ChromaDB or Milvus through the selected provider instead of calling `ChromaDbVectorStore` directly.
- Registered `IVectorStoreManagement` with the same provider selected by `APP_VECTORSTORE_NAME`; Chroma remains the default and Milvus is selected with `APP_VECTORSTORE_NAME=milvus`.
- Added `OpenAiEmbeddingService` and `APP_EMBEDDINGS_PROVIDER=ollama|openai` selection. Ollama remains the default local embedding provider.
- Changed `VectorDocument.Metadata` to `IReadOnlyDictionary<string, object?>` so ingestion can preserve typed metadata before provider-specific serialization.
- Added Python-style metadata schema normalization/validation for .NET ingestor:
  - canonical field types: `string`, `datetime`, `number`, `integer`, `float`, `boolean`, `array`
  - legacy aliases accepted: `str`, `int`, `double`, `bool`
  - typed arrays, datetime UTC `Z` normalization, boolean coercion, required fields, duplicate fields, and reserved field rejection
  - system fields injected: `filename`, `page_number`, `start_time`, `end_time`
- Updated upload behavior to auto-create missing collections before ingestion, matching Python's `upload_documents` behavior.
- Updated `PATCH /documents` vector-store update path to call provider-specific delete and compaction hooks through the management interface.
- Remaining major gaps after this slice: NV-Ingest/NRL adapter, durable ingestion worker/job store, object-store citation/summary assets, backend-persistent catalog/document-info state, and full parity fixtures across ChromaDB and Milvus.

## Blazor Contract/UI Sync Check

- `MetadataFieldDef` now uses Python-compatible metadata type names by default and accepts/normalizes legacy aliases before `POST /collection`.
- The Blazor schema editor was updated with canonical type choices, typed-array selection, optional `max_length`, duplicate-name prevention, and reserved-name prevention for `type`, `subtype`, and `location`.
- `NewCollectionForm.GenerateSummary` now defaults to `true`, matching the React source stores and keeping the `Generate document summaries` checkbox enabled by default.
- Blazor intentionally does not expose controls for `user_defined` or `support_dynamic_filtering`; user-created fields default both to `true`, while server-managed fields are injected and hidden by the ingestor service.
- Current Blazor upload flow sends metadata schema during collection creation. The upload service still accepts a schema parameter but does not send schema in `POST /documents`, which is aligned with the current create-then-upload UI flow.

## .NET Summary Options Validation Slice Completed

- Added API-boundary validation for ingestor `summary_options` so .NET now rejects options when `generate_summary=false`, matching Python's `DocumentUploadRequest` model validator.
- Added Python-compatible `page_filter` validation:
  - accepted strings: `even`, `odd` with normalization to lowercase
  - accepted ranges: non-empty `list[list[int]]` with exactly `[start, end]`
  - rejects zero page numbers, reversed positive ranges, reversed negative ranges, mixed positive/negative ranges, mixed item types, and non-integer range bounds
- Added Python-compatible `summarization_strategy` validation. Explicit values must be `single` or `hierarchical`; omitted strategy still maps to the .NET default iterative summarization path.
- Tightened shared page-filter matching so in-memory range lists behave like JSON range arrays.

## Seven-Item Ingestor Parity Push Completed

- Added request-scoped vector-store factory support. Ingestor operations now create a ChromaDB or Milvus client per request using `vdb_endpoint` and bearer auth from the incoming request instead of relying only on singleton startup configuration.
- Added optional durable ingestion task storage:
  - default remains in-memory
  - setting `APP_INGESTION_TASK_STORE_PATH` enables JSON file-backed task state
  - task state now progresses through `PENDING`, `IN_PROGRESS`, `FINISHED`, or `FAILED`
- Added optional local catalog persistence for collection/document metadata. Setting `APP_INGESTOR_CATALOG_PATH` enables JSON file-backed catalog restore across process restarts.
- Improved delete/update parity by reporting partial document-delete outcomes and keeping update compaction behind the vector-store management interface.
- Replaced stub dependency health with vector-store health checks plus explicit task-store persistence mode and local-ingestion backend capability reporting.
- Added an `IIngestionPipeline` boundary and moved current local text extraction behind `LocalIngestionPipeline`. This does not implement NV-Ingest/NRL runtime behavior, but it creates the explicit plug-in point and reports that the current backend lacks multimodal extraction and object-store asset support.
- Added fixture definitions for request-scoped VDB context, auto-create upload, metadata schema, summary-options validation, partial delete, dependency health, and durable task status.

Remaining hard parity boundary: actual NV-Ingest/NRL execution, multimodal asset extraction, citation object storage, and Python-equivalent document-info aggregation still require either hosting/invoking NV-Ingest/NRL from .NET or implementing a compatible adapter around those services. The .NET code now has a pipeline seam for this instead of embedding local extraction directly in `IngestorService`.

## Remaining-Plan Implementation Update

- Added log-only ingestion telemetry checkpoints through `IIngestionTelemetrySink`. The default sink writes structured `ingestion_checkpoint` log events for ingestion start, per-document validation, vector upsert, object-store citation writes, document failures, and completion. This is intentionally not wired to a collector yet; the interface is the future adapter point for OpenTelemetry, DataDog, or another telemetry store.
- Added shared object-store abstraction plus ingestor filesystem implementation. Setting `APP_OBJECT_STORE_ROOT` enables local JSON payload storage for citation artifacts and summaries; omitting it keeps object storage disabled.
- Summary generation now writes summary payloads to object storage when the object-store implementation is enabled, while retaining the existing vector-store summary collection path.
- Added runtime ingestion backend selection with `APP_INGESTION_BACKEND=local|nvingest|nrl`. `local` remains the working default. `nvingest` and `nrl` now select explicit external-backend adapters that report capabilities but throw a clear unsupported error until real service invocation is implemented.
- Added richer document-info and collection aggregation metrics:
  - document fields include `last_indexed`, `ingestion_backend`, `ingestion_status`, `total_elements`, `raw_text_elements_size`, `has_tables`, `has_charts`, and `has_images`
  - collection fields include `number_of_files`, `doc_type_counts`, `total_elements`, `raw_text_elements_size`, `has_tables`, `has_charts`, `has_images`, `last_indexed`, and `ingestion_status`
- Added fixtures for filesystem object-store citation artifacts, collection document-info metrics, and ingestion telemetry checkpoints.

Remaining true external integration gap: a production `NvIngestPipeline`/`NrlIngestionPipeline` still needs the actual service protocol or SDK contract. The current implementation intentionally fails fast for those backend selections instead of silently pretending local extraction is equivalent.

## External Ingestion Adapter Continuation

- Python does not expose NV-Ingest/NRL as a simple ingestor HTTP API. NV-Ingest is driven through `nv_ingest_client` with a message-client host/port and NRL is invoked as an in-process library pipeline.
- Added a concrete .NET HTTP bridge adapter for external ingestion backends:
  - `APP_INGESTION_BACKEND=nvingest` uses `APP_NVINGEST_ENDPOINT` or `APP_INGESTION_ENDPOINT`
  - `APP_INGESTION_BACKEND=nrl` uses `APP_NRL_ENDPOINT` or `APP_INGESTION_ENDPOINT`
  - optional `APP_INGESTION_API_KEY` is sent as bearer auth
- The bridge posts multipart form data with the document file and backend name, then accepts flexible JSON response fields (`text`, `content`, `extracted_text`, `document_text`, or `raw_text`) plus optional `document_info` and `asset_object_names`.
- `IngestorService` now consumes the richer `IngestionPipelineResult`, so an external bridge can supply Python-like metrics and asset object names without changing the rest of the ingestion workflow.

This makes the .NET side ready to call a thin NV-Ingest/NRL wrapper service, while still avoiding a hard-coded or speculative dependency on Python-only SDK internals.

## Python Bridge Endpoint Added

- Added `POST /bridge/extract` on the Python ingestor server to define the HTTP bridge shape consumed by .NET external ingestion clients.
- Request shape: multipart form with `document` file and optional `backend` field.
- Response shape:
  - `text`: extracted plain text
  - `document_info`: metrics/capability fields including `bridge_mode`, `ingestion_backend`, `total_elements`, `raw_text_elements_size`, and support flags
  - `asset_object_names`: object-store asset names, currently empty
- Current implementation intentionally uses local text extraction and reports `bridge_mode=local_text_extraction`. It does not claim full NV-Ingest/NRL multimodal behavior yet.
- .NET endpoint resolution now accepts either a full bridge URL or a service base URL. If `APP_NVINGEST_ENDPOINT`, `APP_NRL_ENDPOINT`, or `APP_INGESTION_ENDPOINT` has no path, .NET appends `/bridge/extract`.

## Local Milvus/Ollama Verification

- User-provided Milvus 2.6 standalone container exposes health on `9091`, but the v2 REST vector API is served on `19530`.
- Updated `.NET` local env to use `APP_VECTORSTORE_URL=http://localhost:19530` for Milvus REST and `APP_EMBEDDINGS_DIM=384` for Ollama `snowflake-arctic-embed:22m`.
- Updated `MilvusVectorStore` to use POST-based `collections/describe`, POST-based `collections/list` health checks, and Milvus JSON envelope validation (`code == 0`) instead of relying only on HTTP status codes.
- Updated ingestor fixture document paths to files that exist in this checkout and improved the fixture runner to include HTTP error bodies when a service returns a failing status.
- Live `.NET` ingestor run against local Milvus/Ollama passed the fixture runner default ingestor set:
  `ING-COL-001`, `ING-AUTOCREATE-001`, `ING-META-001`, `ING-SUMOPT-001`, `ING-DOC-001`, `ING-DOC-002`, `ING-STS-001`, `ING-DEL-001`, `ING-HEALTH-001`, `ING-METRICS-001`, `ING-OBJSTORE-001`.
- Live `.NET` ingestor run against local ChromaDB/Ollama passed the fixture runner default ingestor set after starting with a clean temp catalog:
  `ING-COL-001`, `ING-AUTOCREATE-001`, `ING-META-001`, `ING-SUMOPT-001`, `ING-DOC-001`, `ING-DOC-002`, `ING-STS-001`, `ING-DEL-001`, `ING-HEALTH-001`, `ING-METRICS-001`, `ING-OBJSTORE-001`, `ING-DUP-001`, `ING-UNSUPPORTED-001`, `ING-UNSUPPORTED-002`.
  The Docker health status initially reported `unhealthy` because the compose healthcheck used a missing `curl` binary; the API itself responded and the .NET fixture run passed. The compose healthcheck was fixed later in this session.

## Additional Fixture Execution

- Extended the ingestor fixture runner with runtime state import/export so restart-sensitive fixtures can preserve captured task ids without hard-coded values.
- Executed and passed `.NET` request-scoped VDB fixture `ING-VDBCTX-001` against local Milvus REST on `19530`.
- Executed and passed `.NET` durable task status fixture `ING-STS-002` by enqueueing `ING-DOC-002`, restarting the ingestor with the same `APP_INGESTION_TASK_STORE_PATH`, and querying the captured task id.
- Strengthened `ING-STS-002` to assert the post-restart state is `FINISHED` and `nv_ingest_status.extraction_completed >= 1`; the stricter fixture passed.
- Executed and passed `OPS-INGEST-TELEMETRY-001`; verified the captured ingestor log contains `ingestion_checkpoint` events for `ingestion.started`, `ingestion.document_validated`, `ingestion.vector_upserted`, and `ingestion.completed`.
- Executed and passed Python bridge fixture `ING-BRIDGE-001` against `POST /bridge/extract`.
- Smoke-tested `.NET` `APP_INGESTION_BACKEND=nvingest` with `APP_NVINGEST_ENDPOINT` pointed to the Python bridge service; upload fixture `ING-AUTOCREATE-001` passed through the external adapter path.

## Milvus Client Hardening

- Added unit coverage for Milvus 2.6 REST behavior:
  - health uses POST `/v2/vectordb/collections/list`
  - collection existence uses POST `/v2/vectordb/collections/describe`
  - nonzero Milvus JSON `code` values are treated as failures even when HTTP status is 200
  - bearer auth is preserved on Milvus requests
  - collection creation uses the configured embedding dimension
- Tightened Milvus delete/drop paths to validate the Milvus JSON response envelope instead of silently accepting HTTP 200 with a nonzero `code`.

## Upload Validation Fixtures

- Added fixtures and sample files for upload validation parity:
  - `ING-DUP-001` validates duplicate filename handling: duplicate files are discarded, one file is processed, and a validation warning is returned.
  - `ING-UNSUPPORTED-001` validates unsupported extension reporting for `.rst` uploads.
  - `ING-UNSUPPORTED-002` validates positive supported-extension filtering for arbitrary unknown extensions such as `.bin`.
- Added pipeline-specific supported-file checks so local extraction only accepts formats it can parse, while external NV-Ingest/NRL bridge adapters can accept a broader backend-compatible extension set.
- External bridge supported extensions now include NRL audio/video types observed in Python (`mp3`, `wav`, `mp4`) so .NET does not reject files that a configured external backend may support.
- Temp upload directory construction now sanitizes the local folder segment derived from `collection_name`; this keeps filesystem storage under the intended upload root without changing the API/vector-store collection name.
- Executed the validation fixtures against live `.NET` ingestor with local Milvus/Ollama; all passed.

## Uploaded Document Response Shape

- Added `.NET` response fields matching Python-style document shape:
  - `document_id`
  - `size_bytes`
- Document IDs are generated during ingestion and stored in document info; reloaded catalog entries fall back to deterministic document-name IDs if older persisted entries do not have a generated id.
- Blazor `DocumentInfo` model now deserializes `document_id` and `size_bytes`, and the collection drawer displays document size when available, keeping the UI contract in sync with the API.
- `ING-AUTOCREATE-001` now asserts the new response fields and passed against live `.NET` ingestor with local Milvus/Ollama.

## .NET Package Advisory Cleanup

- Confirmed `.NET` package versions are managed centrally in `src/dotnet_rag/Directory.Packages.props`.
- `KubernetesClient` and `MessagePack` advisories were transitive through `DotnetRag.AppHost` / `Aspire.Hosting.AppHost`.
- Added central package versions and direct `PrivateAssets="all"` AppHost references for:
  - `KubernetesClient` `17.0.14`
  - `MessagePack` `2.5.302`
- `dotnet list src/dotnet_rag/DotnetRag.sln package --vulnerable --include-transitive` now reports no vulnerable packages for all `.NET` projects.

## ChromaDB Compose Healthcheck

- The local ChromaDB v2 API was healthy and passed .NET fixture execution, but Docker reported the container as `unhealthy`.
- Root cause: `deploy/compose/docker-compose-dotnet.yaml` used `curl --fail http://localhost:8000/api/v2/heartbeat`, while the current `chromadb/chroma:latest` image does not include `curl`, `wget`, or Python on `PATH`.
- Replaced the healthcheck with a Bash `/dev/tcp` HTTP probe that verifies `GET /api/v2/heartbeat` returns an HTTP 200 status.
- `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet` passes after the change.
- Existing running containers keep their old healthcheck until recreated; this fix applies to the next compose recreate.

## Python Reduced Ingestor Baseline Rerun

- The repo skill referenced by `AGENTS.md` (`skills/rag-blueprint/SKILL.md`) is not present in this checkout, so operational work used the local compose/docs/scripts directly.
- Started Python ingestor with:
  - `PYTHONPATH=src`
  - `APP_VECTORSTORE_NAME=milvus`
  - `APP_VECTORSTORE_URL=http://localhost:19530`
  - Ollama embedding/LLM endpoints pointed at local `localhost:11434`
- This resolved the previous ingestor control-plane failure caused by `APP_VECTORSTORE_NAME=chroma`; Python collection creation and metadata-schema fixtures now pass against local Milvus.
- Full Python upload baseline is still not executable in this local runtime because blocking upload enters the NV-Ingest client retry loop against `localhost:7670` (`/v2/submit_job`) and the NV-Ingest runtime is not running.
- Python startup and dependency health also report missing optional/local dependencies:
  - object store unavailable at `localhost:9010`
  - Redis unavailable at `localhost:6379`
  - NV-Ingest unavailable at `localhost:7670`
  - Ollama embedding endpoint responds for model APIs, but Python's NIM health checker probes `/v1/health/ready` and receives `404`
- Reduced Python ingestor baseline results:
  - `ING-COL-001`: pass
  - `ING-META-001`: pass
  - `ING-SUMOPT-001`: pass after updating the fixture to require Python's HTTP `422` validation status
  - `ING-HEALTH-001`: pass for payload shape; dependency entries report the local missing services noted above
  - `ING-BRIDGE-001`: pass
  - `ING-METRICS-001`: fail because local Python `GET /collections` returned `collection_info: {}` for collections without successful Python document-info ingestion
- Updated .NET ingestor upload validation to return HTTP `422 Unprocessable Entity` for invalid `summary_options`, matching Python's FastAPI/Pydantic behavior. Live .NET `ING-SUMOPT-001` passed after this change.
- Full Python upload baseline requires starting `nv-ingest-ms-runtime` and `redis` from `deploy/compose/docker-compose-ingestor-server.yaml`; clean dependency health also requires SeaweedFS from `deploy/compose/vectordb.yaml`.
- Local feasibility check: no `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0` image is present, and neither `NGC_API_KEY` nor `NVIDIA_API_KEY` is exported in this shell. Starting the full NV-Ingest stack would require NGC access or a pre-pulled runtime image.

## RAG/Ollama URL Modernization

- `.NET` RAG health now uses the configured vector-store management implementation instead of hard-coded health URLs. This avoids probing Milvus REST on `19530` with the standalone `/healthz` path that belongs to the Milvus health port.
- `.NET` Ollama runtime calls now use current native endpoints:
  - chat: `POST /api/chat`
  - embeddings: `POST /api/embed`
  - liveness/model availability: `GET /api/tags`
- Legacy Ollama/OpenAI-compatible suffixes such as `/api/embeddings`, `/api/chat`, `/api/embed`, `/api/generate`, `/v1`, and `/v1/embeddings` are still stripped from configured base URLs for compatibility with existing env values.
- The embedding client accepts the legacy single `embedding` response shape only as a fallback, but its primary request/response contract is `/api/embed` with `input` and `embeddings`.
- RAG metrics fixtures now reject scaffold text and accept real Python or `.NET`/OpenTelemetry Prometheus metric families.

## Prompt Catalog And RAG Main Audit

Prompt inventory status:

- `rag_server/prompt.yaml`: migrated. `.NET` now loads the copied YAML through shared `PromptCatalog` from `PROMPT_CONFIG_FILE` or the output `prompt.yaml`.
- `chat_template`: migrated and used by `.NET` no-KB generation.
- `rag_template`: migrated and used by `.NET` KB generation context injection.
- `vlm_template`: migrated and used by `.NET` VLM direct requests as the system prompt.
- `query_rewriter_prompt`: migrated and used by `.NET` `QueryRewritingService`.
- `reflection_relevance_check_prompt`, `reflection_query_rewriter_prompt`, `reflection_groundedness_check_prompt`, `reflection_response_regeneration_prompt`: migrated and used by `.NET` `ReflectionService`.
- `filter_expression_generator_prompt_milvus`: migrated and used by `.NET` `FilterExpressionService`. Elasticsearch prompt remains loaded but unused because the .NET vector-store parity implementation currently supports ChromaDB and Milvus only.
- `filter_expression_generator_prompt_elasticsearch`: copied-not-used.
- `document_summary_prompt`, `shallow_summary_prompt`, `iterative_summary_prompt`: migrated. Summary generation now consumes the shared prompt catalog instead of its separate YAML loader.
- `query_decomposition_multiquery_prompt`: migrated and used by `.NET` query decomposition behind `ENABLE_QUERY_DECOMPOSITION` or per-request `enable_query_decomposition`.
- `query_decompositions_query_rewriter_prompt`, `query_decomposition_followup_question_prompt`, `query_decomposition_final_response_prompt`, `query_decomposition_rag_template`: partial. `.NET` now uses these prompts for contextual subquery rewrite, follow-up detection, per-subquery answers, and final response prompting. Remaining differences are Python's exact recursion orchestration, LangChain streaming/callback behavior, and document score normalization.
- `image_captioning_prompt`: external-bridge-owned. .NET does not perform native NV-Ingest image captioning orchestration.
- `rag_server/agentic_rag/prompt.py`: migrated. `.NET` exposes first-class agentic prompt sections in `PromptCatalog.Agentic` and consumes them through the custom .NET-native Agentic planner/role/orchestration services. The target is behavioral parity with Python Agentic RAG, not adopting LangGraph in .NET.

RAG server audit against `src/nvidia_rag/rag_server/main.py`:

- Request schema/runtime overrides: improved but partial. `.NET` supports the primary generation/search fields, `enable_query_decomposition`, and separate model/sampling knobs for query rewriting, filter generation, and reflection. Python still has broader per-role endpoint/API-key overrides, VLM/reasoning controls, and richer validation.
- No-KB chat flow: improved. `.NET` now applies `chat_template`; remaining differences include Python's full chain/callback/token-usage handling and VLM direct branching details.
- KB RAG flow: improved. `.NET` now applies `rag_template` and searches all requested `collection_names` for standard RAG. Retrieval remains simplified compared with Python's multi-collection validation, VDB operator cleanup, and richer error handling.
- Query rewriting: improved. `.NET` now uses YAML prompts and honors per-request or environment enablement.
- Query decomposition: improved but still partial. `.NET` now does YAML-driven subquery generation, contextual rewrite, per-subquery answer generation, follow-up detection up to `MAX_RECURSION_DEPTH`, original-query retrieval, context merge, final-response prompting, and raw reranker-score normalization before confidence filtering. Remaining differences include Python's exact iterative control flow, single-collection enforcement, and LangChain stream/callback behavior.
- Filter generation: improved through vector-store filter capabilities. `.NET` uses `IVectorStoreFilterCapabilities` so concrete providers own whether generated filters are supported and which prompt syntax they require. Milvus opts into generated filters with the Milvus YAML prompt; ChromaDB opts out of generated filters but handles simple explicit metadata filters in its own `SearchAsync` implementation.
- Reflection: improved. `.NET` uses YAML reflection prompts for context relevance, query rewrite, groundedness, and regeneration, and honors `REFLECTION_LLM` plus Python threshold/max-loop env aliases. Python still has separate reflection endpoint/API-key configuration and more detailed response counters/telemetry.
- VLM direct flow and fallback: improved. `.NET` injects the YAML VLM system prompt and keeps the existing VLM-to-LLM fallback. Python still has richer image/document-context handling and thinking-token controls.
- Citations/search response shape: partial. `.NET` response shape is compatible enough for local fixtures, but Python citation generation has richer source metadata, expanded context handling, and image/table asset semantics.
- Confidence threshold/reranker behavior: improved but partial. `.NET` now applies confidence thresholds after reranking and warns/skips confidence filtering when reranker scores are unavailable. Query decomposition also normalizes raw reranker scores with Python-equivalent sigmoid scaling when scores are outside the normalized 0..1 range.
- Streaming vs non-streaming behavior: partial. `.NET` buffers then emits SSE chunks, especially for guardrails/reflection, while Python streams from LangChain chains with eager prefetch, token callbacks, and richer final-event metadata.
- Agentic RAG: superseded by later slices. At this point in the history `.NET` returned HTTP 501 and hid the Blazor selector; current state is tracked in the inventory and Current Remaining Plan below.

.NET implementation completed in this slice:

- Added `DotnetRag.Shared.Prompts.PromptCatalog` with typed sections for chat, RAG, VLM, query rewriting, reflection, filtering, query decomposition, summarization, image captioning, and centralized agentic prompt resources.
- Registered the catalog in shared DI and replaced hard-coded prompt constants in RAG, query rewriting, reflection, filtering, and summarization paths.
- Added a regression guard that compares the checked-in `.NET` `src/dotnet_rag/utils/prompt.yaml` copy with Python's `src/nvidia_rag/rag_server/prompt.yaml`, normalizing line endings so content drift is caught without forcing unrelated newline churn.
- Added `QueryDecompositionService` using the YAML multi-query, contextual rewrite, subquery RAG, follow-up, and final-response prompts with merged retrieval contexts.
- Added `ENABLE_QUERY_DECOMPOSITION` and per-request `enable_query_decomposition` to .NET config/contracts.
- Added standard multi-collection RAG/search retrieval and per-collection generated Milvus filters; chunks now carry `collection_name` metadata.
- Added query-rewriter, filter-generator, and reflection model configuration support using Python-compatible env names where applicable.
- Added vector-store filter capability abstraction and DI registration. This removed provider-name checks from filter generation orchestration and keeps ChromaDB/Milvus behavior behind provider implementations.
- Added ChromaDB explicit filter translation for simple metadata clauses such as `content_metadata["year"] == "2024"` into Chroma `where` payloads; generated filters remain disabled for ChromaDB.
- Synced Blazor with backend behavior by exposing Query Decomposition in Feature Toggles, tracking `vector_store_provider` from `/configuration`, describing Filter Generator as Milvus-backed when appropriate, and no longer suppressing the filter-generator request flag for multi-collection selection.
- Synced `.NET` compose env files with query decomposition, filter generator, and reflection/query-rewriter model settings.
- Explicitly marked agentic RAG unavailable in the .NET server and hid the Blazor Agentic option. Superseded by later gated Agentic implementation work below.
- Added unit tests for prompt loading, custom prompt overrides, agentic prompt presence, YAML-backed rendering in query rewriting/filter generation, direct RAG/chat/VLM prompt behavior, query-decomposition final prompt behavior, multi-collection RAG retrieval, per-role model overrides, vector-store filter capabilities, ChromaDB explicit filter translation, and explicit agentic unavailability.

Validation:

- `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 80 tests.
- `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
- `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
- `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.
- Live `.NET` RAG fixture run against local Milvus/Ollama passed `RAG-HEALTH-001`, `RAG-METRICS-001`, and `RAG-SRCH-001`; server logs confirmed search called Ollama at `POST http://localhost:11434/api/embed`.

## .NET RAG Failure and Trace Fixture Automation

- Added `fixtures/run_dotnet_rag_fixtures.py` to start an isolated `.NET` `rag_server` process with scenario-specific env overrides, run selected API fixtures, and shut the process down cleanly.
- The runner has deterministic scenarios for:
  - `normal`: local Milvus/Ollama baseline
  - `bad-reranker`: unreachable reranker service to validate backend failure mapping
  - `bad-vdb`: unreachable vector-store endpoint to validate backend failure mapping
  - `tracing`: local OTLP/HTTP capture server plus `APP_TRACING_ENABLED=true`
- Isolated `.NET` RAG runs passed:
  - `FAIL-VDB-001` with `APP_VECTORSTORE_URL=http://127.0.0.1:9`
  - `FAIL-RERANK-001` with `APP_RERANKER_SERVICE_URL=http://127.0.0.1:9`
  - full RAG API set: `RAG-HEALTH-001`, `RAG-METRICS-001`, `RAG-SRCH-001`, `RAG-GEN-001`, `RAG-SUM-001`
  - `OPS-TRACE-001` with two captured OTLP/HTTP export requests containing `.NET` RAG service/span identifiers
- Later live Python parity work changed the canonical reranker-outage behavior from graceful fallback to backend-unavailable `5xx`; the runner scenario name remains useful, but the fixture expectation now asserts backend failure.

## Summarization Vector Store Decoupling

- `SummarizationService` still had a concrete `ChromaDbVectorStore chromaStore` constructor dependency, which violated the interface-driven Milvus/Chroma provider design.
- Added `IVectorDocumentLookup` for the summary retrieval capability that was previously Chroma-specific (`GetDocumentTextByIdAsync`).
- `ChromaDbVectorStore` and `MilvusVectorStore` now implement `IVectorDocumentLookup`.
- `SummarizationService` now depends on:
  - `IVectorStore` for summary upserts
  - `IVectorStoreManagement` for summary collection creation
  - `IVectorDocumentLookup` for summary retrieval by id
  - `IObjectStore` for optional JSON artifact storage
- DI now registers `IVectorDocumentLookup` to the selected vector-store provider, so summaries follow the configured ChromaDB or Milvus backend instead of always using Chroma.
- Added unit coverage for Milvus summary lookup by id through the REST `entities/query` path.

## Python RAG Local Baseline Correction

- Python RAG should not be treated as an Ollama-native local baseline. Its current source implementation uses NVIDIA/OpenAI-compatible endpoint wrappers and NIM health paths, so pointing it at Ollama's native API creates invalid failures.
- Future local Python RAG parity runs should use deterministic mocked/model-inferred responses or an OpenAI-compatible mock server that emulates the expected NVIDIA endpoint contract. This keeps Python as the source API shape without conflating it with Ollama local-dev transport details.
- The attempted Python+Ollama run did reveal a separate vector-schema mismatch: Python LangChain Milvus expects the canonical vector field `vector`, while the earlier .NET-created Milvus collection used `embedding`.
- Updated `.NET` Milvus collection creation to use Python's canonical `vector` field and `idx_vector` index for new collections. Existing local `.NET` collections that still have `embedding` remain readable/searchable through field detection fallback.
- Added `fixtures/mock_nim_server.py` and `fixtures/run_python_rag_mock_fixtures.py` to run Python RAG against deterministic OpenAI/NIM-compatible local endpoints instead of Ollama.
- Mocked Python RAG baseline passed:
  - `RAG-HEALTH-001`
  - `RAG-GEN-MOCK-001`
  - `RAG-SRCH-MOCK-001`
- The mock runner seeds a dedicated `parity_mock_data` Milvus collection using Python/NV-Ingest-compatible fields (`pk`, `vector`, `source`, `content_metadata`, `text`) and deterministic 384-dimensional embeddings.

## Milvus Schema Parity Validation

- Confirmed `nv_ingest_client.util.milvus.create_nvingest_schema` creates canonical fields: `pk`, `vector`, `source`, `content_metadata`, and `text`.
- Updated `.NET` Milvus collection creation to include those Python/NV-Ingest fields plus `.NET` compatibility fields `id` and `metadata`.
- Milvus REST collection creation did not honor auto-id for `pk` in this local 2.6 REST path, so `.NET` now supplies deterministic positive `Int64` `pk` values during upsert.
- Live `.NET` ingestor validation created `dotnet_schema_parity` and inserted a document successfully. Milvus describe returned fields `pk`, `id`, `text`, `vector`, `source`, `content_metadata`, `metadata` with `idx_vector` on `vector`.

## Summary End-to-End Provider Validation

- Live Milvus summary flow passed:
  - `.NET` ingestor uploaded `summary-milvus.txt` with `generate_summary=true` into `summary_milvus_parity`.
  - `.NET` RAG `/summary?collection_name=summary_milvus_parity&file_name=summary-milvus.txt&blocking=true` returned `status=SUCCESS`.
- Live ChromaDB summary flow passed:
  - `.NET` ingestor uploaded `summary-chroma.txt` with `generate_summary=true` into `summary_chroma_parity`.
  - `.NET` RAG `/summary?collection_name=summary_chroma_parity&file_name=summary-chroma.txt&blocking=true` returned `status=SUCCESS`.
- This validates that `SummarizationService` is now provider-selected through interfaces rather than Chroma-only.

## Items 4-7 Follow-Up

- Item 4, external ingestion output shape:
  - Tightened the `.NET` ingestion service so the first pipeline extraction result is reused for vector chunking and summary preparation. This avoids calling an external NV-Ingest/NRL bridge multiple times for the same upload and keeps bridge-supplied text, document-info metrics, and asset names internally consistent.
  - The Python `/bridge/extract` response now reports additional Python-like document-info aggregation fields: `doc_type_counts`, `number_of_files`, `size_bytes`, `last_indexed`, and `ingestion_status`.
  - `.NET` filesystem object storage now writes an `asset_manifest.json` beside `citation.json` when an external bridge reports `asset_object_names`.
- Item 5, request-scoped VDB endpoint/auth:
  - `POST /collection` and `POST /collections` now use the same request-scoped vector-store client factory path as upload/list/delete, so `vdb_endpoint` and `Authorization: Bearer ...` are both available to collection creation.
- Item 6, durable catalog/document-info:
  - Existing file-backed catalog persistence remains the durable option via `APP_INGESTOR_CATALOG_PATH`; this slice preserves bridge-supplied document info in the same catalog path rather than discarding it after upload.
- Item 7, fixture enforcement:
  - The ingestor fixture runner now enforces `side_effect_assertions`, including object-store artifact existence checks under an env-provided root.
  - `ING-BRIDGE-001` now asserts the richer bridge document-info fields instead of only checking that a generic object exists.

## Worker Split And External Bridge Continuation

- Ordering:
  - Phase 2 worker split can be implemented before live NV-Ingest/NRL validation because it moves job execution behind a queue while preserving the same `IIngestionPipeline` abstraction.
  - Real NV-Ingest/NRL live validation cannot be completed without an available runtime endpoint. The code can be made ready and the validation command can be added now, but a pass/fail runtime result requires NV-Ingest/NRL services, model endpoints, and object/vector-store dependencies to be running.
- Worker split implementation:
  - Added `IIngestionJobQueue` with in-memory and file-backed implementations.
  - Setting `APP_INGESTION_EXECUTION_MODE=queued` makes non-blocking upload persist a queued `IngestionJob` instead of executing in the API process.
  - `IngestionWorkerService` claims queued jobs, executes the existing ingestion pipeline, and updates the shared `IIngestionTaskStore` so `/status` continues to work from the API service.
  - `APP_INGESTOR_ROLE=api|worker|all` controls whether the ingestor process hosts the worker. Local default remains compatible; Docker compose runs a fourth service named `dotnet-ingestion-worker`.
  - `docker-compose-dotnet.yaml` now shares `dotnet-ingestor-data` at `/tmp-data` between API and worker containers for uploaded files, job queue, task status, catalog, and object-store artifacts.
- Real bridge implementation:
  - Python `/bridge/extract` now has opt-in real backend paths:
    - `APP_BRIDGE_USE_REAL_NVINGEST=true` routes `backend=nvingest` through `nv_ingest_client` with `vdb_op=None`.
    - `APP_BRIDGE_USE_REAL_NRL=true` routes `backend=nrl` through `NemoRetrieverHandler.ingest_shallow`.
  - Without those flags the bridge keeps deterministic local text extraction for development and fixture stability.
  - Added `fixtures/run_external_ingestion_bridge_validation.py`; it validates a configured bridge endpoint and skips cleanly when no external endpoint is configured.
- Optional backend-persistent catalog/document-info parity:
  - Current `.NET` durable catalog parity is file-backed JSON (`APP_INGESTOR_CATALOG_PATH`). This preserves .NET-created collections/doc-info across restarts and works for the split worker.
  - Full Python-equivalent backend persistence would store catalog metadata, metadata schemas, and document-info aggregation in provider-managed backing collections/tables, not in the API process or a JSON sidecar.
  - For Milvus/Chroma, the pragmatic next interface would be an `IIngestorCatalogStore` with file, vector-store, and possibly SQL/Redis implementations. The vector-store implementation should read/write the same logical records Python uses for collection info and document info, then `InMemoryIngestorStore` becomes a cache rather than the source of truth.
  - This is valuable if Python and .NET must interoperate over collections created by either stack, or if multiple .NET API instances need shared catalog state without a shared filesystem.
- Full Python baseline rerun with NV-Ingest/Redis/SeaweedFS:
  - Required services: Python ingestor, `nv-ingest-ms-runtime` on its configured message-client host/port, Redis if `ENABLE_REDIS_BACKEND=True`, SeaweedFS/S3-compatible object store if object-store health and citation assets are in scope, configured vector DB, and model endpoints matching Python's NVIDIA/OpenAI-compatible health/API paths.
  - The rerun should execute the full ingestor fixture set, not only reduced control-plane fixtures: blocking upload, non-blocking upload/status, metadata schema, duplicate/unsupported handling, delete/update, summaries, object-store citation assets, metrics/collection info, health, and bridge extraction.
  - The local blocker remains that the NV-Ingest runtime image/service is not available in this checkout environment; without it, Python upload fixtures enter the retry loop against `localhost:7670`.
  - Added `fixtures/run_python_full_baseline.py` to run dependency preflight and optionally execute the full Python ingestor fixture set when dependencies pass. The runner checks Python ingestor, Redis, NV-Ingest, object store, vector store, and embedding endpoint separately.
  - Current local preflight result: vector store passed; Python ingestor, Redis, NV-Ingest, and object store were not listening; embedding endpoint was skipped because `APP_EMBEDDINGS_SERVERURL` was not set. When the user starts Redis locally on `localhost:6379`, rerunning this preflight should isolate the remaining blockers to Python ingestor/NV-Ingest/object store/model configuration.
  - Redis local command can be a simple container such as `docker run --rm --name abes-redis -p 6379:6379 redis/redis-stack:7.2.0-v18`.

## Catalog Store Boundary

- Added `IIngestorCatalogStore` so `.NET` catalog/document-info persistence is no longer hard-coded inside `InMemoryIngestorStore`.
- Current implementations:
  - `DisabledIngestorCatalogStore` for true in-memory default behavior.
  - `FileBackedIngestorCatalogStore` selected by `APP_INGESTOR_CATALOG_PATH`.
- `InMemoryIngestorStore` now acts as the API-process cache over the selected catalog store. This keeps the existing API behavior stable and provides the extension point for a future vector-store/Redis/SQL-backed `IIngestorCatalogStore`.
- Full Python-shared catalog parity should be implemented as another `IIngestorCatalogStore` that reads/writes the same logical metadata-schema and document-info records Python stores through its VDB operator. That work should be done once the target shared backend is selected, because Milvus/Chroma/Elasticsearch have materially different query/update primitives for arbitrary catalog records.

## Runtime Validation Continuation

- Started local Redis as `abes-redis` on `localhost:6379` using the locally available `redis:8.6-alpine` image.
- Started Python ingestor on `127.0.0.1:18097` with Milvus/Ollama-oriented local settings and Redis enabled.
- Full Python baseline preflight improved:
  - `python_ingestor`: pass
  - `redis`: pass (`PONG`)
  - `vectorstore`: pass
  - `embedding_model`: pass
  - `nvingest`: fail, `localhost:7670` not listening
  - `object_store`: fail, `localhost:9010` not listening
- Reduced Python ingestor control/bridge fixtures passed with Redis enabled:
  - `ING-COL-001`
  - `ING-META-001`
  - `ING-SUMOPT-001`
  - `ING-HEALTH-001`
  - `ING-BRIDGE-001`
- Live external bridge validation passed against the Python bridge endpoint at `http://127.0.0.1:18097/bridge/extract` using `backend=nvingest` in local bridge mode. The real NV-Ingest branch still requires `APP_BRIDGE_USE_REAL_NVINGEST=true` on a Python bridge process plus NV-Ingest runtime on `7670`.
- `.NET` queued-worker validation:
  - Started `.NET` ingestor in `APP_INGESTION_EXECUTION_MODE=queued`, `APP_INGESTOR_ROLE=all` on `127.0.0.1:18098`.
  - `ING-DOC-002` and `ING-STS-001` passed against queued mode.
  - Direct post-worker status returned `state=FINISHED`, `documents_completed=1`, and `nv_ingest_status.extraction_completed=1`.
- Milvus schema adaptation fix:
  - Queued validation first exposed that existing Python/NV-Ingest-created Milvus collections can reject .NET compatibility fields (`id`, `metadata`) when dynamic fields are unavailable.
  - Updated `.NET` Milvus upsert/search/list/delete paths to inspect target collection fields and emit/query only fields present in the schema.
  - Added unit coverage for Python/NV-Ingest canonical Milvus schema so .NET omits `id` and `metadata` when the collection only has `pk`, `text`, `vector`, `source`, and `content_metadata`.

## Object Store Runtime Continuation

- Started SeaweedFS from `deploy/compose/vectordb.yaml` on `localhost:9010`/`9011`.
- Restarted Python ingestor with:
  - `OBJECTSTORE_ENDPOINT=localhost:9010`
  - `OBJECTSTORE_ACCESSKEY=seaweedfsadmin`
  - `OBJECTSTORE_SECRETKEY=seaweedfsadmin`
- Python ingestor successfully created/confirmed the `default-bucket` object-store bucket.
- Full Python baseline preflight now passes every local dependency except NV-Ingest:
  - `python_ingestor`: pass
  - `redis`: pass
  - `object_store`: pass
  - `vectorstore`: pass
  - `embedding_model`: pass
  - `nvingest`: fail, `localhost:7670` not listening
- Reduced Python control/bridge fixtures still pass with Redis and object store enabled:
  - `ING-COL-001`
  - `ING-META-001`
  - `ING-SUMOPT-001`
  - `ING-HEALTH-001`
  - `ING-BRIDGE-001`
- Attempted to start NV-Ingest:
  - `docker compose -f deploy/compose/docker-compose-ingestor-server.yaml up -d nv-ingest-ms-runtime` is blocked by missing required `NGC_API_KEY`.
  - Direct `docker pull nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0` fails with NVCR `Access Denied`.
- Remaining full Python baseline blocker is therefore external NVCR/NGC access or a pre-pulled `nv-ingest:26.3.0` runtime image.

## Blazor Local Storage Configuration

- `UseLocalStorage` must be false for the `.NET` Blazor frontend.
- The setting lives under the Blazor `Settings` configuration section, so compose/env overrides must use `Settings__UseLocalStorage=false`.
- Updated `SettingsState` to actually read `Settings:UseLocalStorage`; previously the appsettings/env value existed but the state default stayed enabled.

## Blazor VLM Model Display

- The Settings > Model Configuration tab should expose `VLM Model`, not `VLM Provider`, because provider selection is a service-routing concern while the model tab is for model names.
- Backend configuration remains correctly split: `models.vlm_model` populates `SettingsState.VlmModelName`, and `providers.vlm_provider` remains available for Ollama/OpenAI service routing.

## Notebook-Derived .NET Test Approach

- The local checkout contains one notebook file, `deploy/workbench/quickstart.ipynb`; the broader Python notebook suite is referenced from `docs/notebooks.md` as upstream GitHub notebooks.
- The .NET validation path should use a dedicated notebook copy instead of the Python Workbench notebook because the .NET API test surface should exercise unversioned routes.
- Added `deploy/workbench/quickstart-dotnet.ipynb` as the .NET notebook smoke workflow:
  - RAG routes: `/health`, `/configuration`, `/chat/completions`, `/search`, `/summary`.
  - Ingestor routes: `/health`, `/collections`, `/documents`.
  - RAG requests use `collection_names` rather than Python notebook `collection_name` aliases.
  - Local dev defaults target ChromaDB at `http://localhost:8000`, with `DOTNET_VDB_ENDPOINT=http://localhost:19530` as the Milvus parity switch.
- Updated the existing .NET health/config integration test to use unversioned routes only; the notebook and touched tests now contain no `/v1` or `/v2` endpoint calls.

## .NET Prompt, UI, and Vector-Store Capability Continuation

- Shared prompt handling is now centralized in `PromptCatalog`, including Python `prompt.yaml` sections and embedded agentic prompt resources.
- RAG, chat, VLM, query rewriting, reflection, filter generation, summarization, and query decomposition now consume prompt catalog sections instead of service-local prompt constants.
- Query decomposition is implemented for .NET KB-backed generation and exposed through the Blazor settings model/request contract. Agentic RAG was explicitly unavailable at this point in the history; superseded by later custom .NET-native Agentic runtime slices below.
- Standard .NET RAG retrieval now searches all requested `collection_names`; query decomposition keeps the Python single-collection limitation and logs when extra collections are ignored.
- Filter generation is interface-driven through `IVectorStoreFilterCapabilities`:
  - Milvus opts into generated filter prompts and provides provider-specific schema text.
  - ChromaDB does not opt into generated filters, but translates simple explicit metadata filter expressions to native Chroma `where` filters.
- Blazor sends filter/query-decomposition settings to the server and displays provider-aware filter-generator text. The backend remains responsible for deciding whether the active vector store can execute generated filters.
- Streaming citation payloads now include `document_id`, `content`, `text`, `source`, `document_name`, `collection_name`, `document_type`, `score`, and `total_results` so Blazor can render source snippets and collection badges while preserving existing frontend-compatible fields.

## .NET Role-Specific LLM Dependency Sync

- Added .NET configuration for Python-compatible role dependency variables:
  - `APP_QUERYREWRITER_SERVERURL`, `APP_QUERYREWRITER_APIKEY`
  - `APP_FILTEREXPRESSIONGENERATOR_SERVERURL`, `APP_FILTEREXPRESSIONGENERATOR_APIKEY`
  - `REFLECTION_LLM_SERVERURL`, `REFLECTION_LLM_APIKEY`
- The role model variables already existed (`APP_QUERYREWRITER_MODELNAME`, `APP_FILTEREXPRESSIONGENERATOR_MODELNAME`, `REFLECTION_LLM`); each role now has effective endpoint/API-key defaults that fall back to the main LLM endpoint and `NVIDIA_API_KEY` when role-specific values are blank.
- Registered keyed `IChatCompletionService` instances for `query_rewriter`, `filter_expression_generator`, and `reflection` in shared infrastructure. The concrete provider is still selected behind the interface:
  - blank role endpoint uses the main `APP_LLM_PROVIDER`;
  - explicit Ollama-looking endpoints use Ollama;
  - other explicit role endpoints use OpenAI-compatible chat.
- RAG service composition now injects role-specific chat clients into query rewriting, query decomposition, filter generation, and reflection without changing those services away from `IChatCompletionService`.
- Synced `deploy/compose/dotnet-local.env` and `deploy/compose/dotnet-docker.env` with the role endpoint/API-key variables.
- `/configuration` now returns the effective role models and endpoints for query rewriting, filter generation, and reflection. Blazor's settings state and Models/Endpoints panels consume those fields so the UI reflects role-specific dependencies while keeping API keys server-side.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 81 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests, after rerunning separately from the full solution build. The first concurrent run failed on a locked `MvcTestingAppManifest.json` generated by `Microsoft.AspNetCore.Mvc.Testing`.
- Validation after adding `/configuration` and Blazor role dependency visibility:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 81 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Request-Scoped Vector Store Runtime Overrides

- RAG generation/search now resolve a request-scoped vector store when `vdb_endpoint` or a bearer `Authorization` header is present, using the existing `IVectorStoreClientFactory`.
- The request-scoped client carries provider filter capabilities alongside `IVectorStore`/`IVectorStoreManagement`, so generated filter decisions remain owned by the concrete vector store implementation for the selected runtime endpoint.
- Query decomposition now accepts the selected `IVectorStore` so `vdb_endpoint` overrides do not silently fall back to the singleton vector store during decomposed retrieval.
- ChromaDB and Milvus compatibility stays behind interfaces:
  - ChromaDB request clients keep generated filters disabled and apply simple explicit filters through Chroma `where`.
  - Milvus request clients keep generated filters enabled and use Milvus schema/filter semantics.
- Remaining runtime override gaps: request-level LLM/embedding/reranker/VLM endpoint fields are still mostly model/request metadata in `.NET`; only vector-store endpoint/auth now has request-scoped provider selection in RAG retrieval.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 82 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Request-Scoped Chat Runtime Overrides

- Added `IChatCompletionClientFactory` and a shared `ChatCompletionClientFactory` that creates Ollama or OpenAI-compatible `IChatCompletionService` instances from a provider/model/endpoint tuple.
- RAG answer generation now honors request-level `llm_endpoint` for both non-streaming `/chat/completions` and streaming `/generate`.
- VLM generation now honors request-level `vlm_endpoint`; VLM-to-LLM fallback uses request-level `llm_endpoint` when present.
- Provider selection stays interface-based:
  - explicit Ollama-looking endpoints use Ollama;
  - explicit `/v1` or `chat/completions` endpoints use OpenAI-compatible chat;
  - otherwise the configured provider is used.
- Request-level `embedding_endpoint` / `embedding_model` now create request-scoped embedding clients inside `IVectorStoreClientFactory`, so retrieval query embedding follows the request override for ChromaDB and Milvus.
- Request-level `reranker_endpoint` now flows through normal RAG search/generation and query decomposition reranking via `IRerankerClient`.
- Historical note: this slice initially left the OpenAI-compatible vector-store search route narrower than normal RAG/search. A later slice added its reranker, vector-store, embedding, query-rewriter, and explicit filter override support.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 83 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Request-Scoped Embedding and Reranker Runtime Overrides

- Extended `IVectorStoreClientFactory.Create(...)` to accept request-level `embedding_endpoint` and `embedding_model` overrides.
- The vector-store factory now creates a request-scoped `IEmbeddingService` for Ollama or OpenAI-compatible embedding endpoints when either embedding override is present.
- RAG generation/search pass `embedding_endpoint` and `embedding_model` into the vector-store resolver, so ChromaDB and Milvus query embedding stays provider-specific and interface-based.
- Extended `IRerankerClient.RerankAsync(...)` with a source-compatible optional endpoint override.
- Normal RAG search, generation, query decomposition, and the OpenAI-compatible vector-store search route now pass request-level `reranker_endpoint` into reranking.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 85 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Vector-Store Search Reranker Override Completion

- Added `reranker_endpoint` to `VectorStoreSearchRequest` so `/v2/vector_stores/{vector_store_id}/search` can route reranking to a request-selected reranker service.
- Added unit coverage for the vector-store search route forwarding the override to `IRerankerClient`.
- Query decomposition internal LLM calls now use `QueryRewriterModelOrDefault`, matching the query-rewriter role client/endpoint used by DI. Final answer generation still uses the normal answer-generation model.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 86 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.
  - Revalidated after query-decomposition model routing cleanup with the same pass results.

## .NET ChromaDB Explicit Filter Compatibility

- Expanded ChromaDB's concrete vector-store filter translation while keeping generated filter orchestration interface-driven:
  - ChromaDB still reports `SupportsGeneratedFilters == false` and does not consume LLM-generated Milvus/Elasticsearch filter prompts.
  - ChromaDB now translates additional explicit metadata filters itself, including parenthesized `AND` expressions, lowercase `and`, unquoted numeric/boolean values, and Python-style `content_metadata["field"]` clauses.
  - Unsupported ChromaDB filter expressions continue to be ignored instead of being sent to Chroma as invalid `where` payloads.
- Added unit coverage for ChromaDB provider behavior so filtering remains implemented by the concrete vector DB adapter rather than by RAG orchestration code.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore --filter ChromaDbVectorStoreTests`: passed, 4 tests.
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 88 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Request-Scoped Role Dependency Overrides

- Added generation/search request fields for role-specific dependency overrides:
  - `query_rewriter_model`, `query_rewriter_endpoint`, `query_rewriter_api_key`
  - `filter_expression_generator_model`, `filter_expression_generator_endpoint`, `filter_expression_generator_api_key`
  - `reflection_model`, `reflection_endpoint`, `reflection_api_key` for generation requests
- RAG orchestration now creates request-scoped role services through `IChatCompletionClientFactory` when a role override is present; otherwise the configured singleton keyed services are reused.
- Query rewriting and query decomposition share the request-selected query-rewriter client/model. Filter generation uses the request-selected filter-generator client/model while vector-store filter capability decisions remain owned by `IVectorStoreFilterCapabilities`. Reflection relevance and groundedness checks use the request-selected reflection client/model.
- Blazor now sends the role model/endpoint settings that it already displays, so UI changes are reflected in generation requests. API keys remain server-side in the UI path; raw API callers can pass explicit per-request role API keys.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 89 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET OpenAI-Compatible Vector-Store Search Parity

- Extended `/v2/vector_stores/{vector_store_id}/search` to participate in the same interface-based runtime override model as normal RAG/search:
  - `vdb_endpoint` plus bearer auth now resolve a request-scoped vector-store client through `IVectorStoreClientFactory`.
  - `embedding_endpoint` / `embedding_model` flow into that request-scoped vector-store client so query embedding follows the selected runtime dependency.
  - `rewrite_query` now uses the request-selected query-rewriter role client/model when `query_rewriter_model`, `query_rewriter_endpoint`, or `query_rewriter_api_key` are supplied.
  - OpenAI-style comparison and compound `filters` deserialize from JSON and are translated to provider-facing metadata filter expressions before calling `IVectorStore.SearchAsync`.
  - `ranking_options.score_threshold` filters candidates before reranking, and `reranker_endpoint` continues to route reranking through `IRerankerClient`.
- Filtering remains vector-store owned: the route produces an explicit filter expression, then ChromaDB/Milvus concrete adapters decide how to execute or ignore it through their own `SearchAsync` implementations.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 92 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Search Citation Attribute Parity

- Consolidated search result shaping for normal `/search` and OpenAI-compatible `/v2/vector_stores/{vector_store_id}/search`.
- Search result `attributes` now include citation-compatible fields derived from vector-store metadata and result content:
  - `document_id`
  - `content`
  - `text`
  - `source`
  - `document_name`
  - `collection_name`
  - `document_type`
  - `score`
- The OpenAI-compatible vector-store route falls back to the route `vector_store_id` as `collection_name` when the concrete vector store did not provide collection metadata.
- This keeps response shaping centralized while ChromaDB/Milvus continue to own metadata retrieval and filter behavior behind `IVectorStore`.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 92 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Confidence Threshold and Reranker Ordering

- Moved confidence-threshold filtering to run after reranking when reranking is enabled, matching Python's reranker-score based confidence filtering.
- Normal RAG generation, `/search`, OpenAI-compatible vector-store search, and query decomposition no longer pre-filter candidates by vector-store score before reranking.
- When `confidence_threshold > 0` but reranking is disabled or the reranker is unavailable, `.NET` now logs a warning and returns vector-score ordering without confidence filtering instead of silently treating vector similarity as a reranker relevance score.
- Query decomposition uses the same post-rerank threshold behavior and warning path.
- Added unit coverage for:
  - low vector-score candidates reaching the reranker and being kept when reranker relevance exceeds the threshold;
  - confidence thresholds not filtering vector scores when reranking is disabled.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 94 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET RAG Request Validation Parity

- Added route-level validation for Python-compatible retrieval settings:
  - `vdb_top_k` must be greater than 0 for generation and `/search`.
  - `reranker_top_k` must be greater than 0 and less than or equal to `vdb_top_k`.
  - `confidence_threshold` / `ranking_options.score_threshold` must be in `[0.0, 1.0]`.
  - OpenAI-compatible vector-store `max_num_results` must be greater than 0.
  - OpenAI-compatible `ranking_options.ranker` accepts `auto`, `true`, `on`, `enabled`, `none`, `false`, `off`, and `disabled`; unknown values return HTTP 400.
- `/v2/vector_stores/{vector_store_id}/search` now honors `ranking_options.ranker=none` by bypassing reranking even when the server default reranker is enabled.
- Added unit coverage for invalid top-k, invalid confidence threshold, invalid ranker value, and ranker-disabled vector-store search behavior.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 98 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Query Decomposition Score Normalization

- Added query-decomposition reranker-score normalization before confidence filtering.
- The implementation mirrors Python's sigmoid scaling (`1 / (1 + exp(-(score * 0.1)))`) for raw reranker logits, while leaving already-normalized `0..1` scores unchanged for compatibility with the local `.NET` reranker-service contract.
- Added regression coverage proving that raw reranker scores are normalized before applying `confidence_threshold`, so a raw score that would pass without normalization is excluded after sigmoid normalization.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 99 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Non-Streaming Citation Payload Parity

- Added citation payloads to non-streaming `/chat/completions` RAG responses.
- Plain no-KB `/chat/completions` responses remain OpenAI-style and do not include a `citations` field.
- The non-streaming response now includes the same citation envelope used by the streaming final SSE event:
  - `total_results`
  - `results[].document_id`
  - `results[].content`
  - `results[].text`
  - `results[].source`
  - `results[].document_name`
  - `results[].collection_name`
  - `results[].document_type`
  - `results[].score`
- Shared the citation payload builder between streaming and non-streaming paths so response-shape changes stay synchronized.
- Added regression coverage for non-streaming citation fields, including collection metadata from multi-collection retrieval and document type propagation from vector-store metadata.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 100 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Citation Toggle and Empty Envelope Parity

- Generation now respects `enable_citations=false` for both streaming `/generate` and non-streaming `/chat/completions` RAG responses.
- Retrieved context is still included in the LLM prompt when citations are disabled; only the outward citation payload is suppressed to `{ total_results: 0, results: [] }`.
- VLM streaming final events now use the shared empty citation envelope and include `total_results: 0`, matching the normal streaming response shape.
- Added regression coverage for:
  - non-streaming RAG with citations disabled;
  - streaming RAG with citations disabled;
  - VLM streaming empty citation envelope shape.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 103 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET VLM and Reasoning Control Parity

- Wired standard LLM thinking controls from `min_thinking_tokens` / `max_thinking_tokens` into the shared `ChatCompletionRequest`.
- Wired VLM request controls from `vlm_enable_thinking` and `vlm_thinking_token_budget` into the same provider-neutral request path.
- OpenAI-compatible chat calls now serialize Python/NIM-compatible reasoning fields as top-level request JSON:
  - `chat_template_kwargs.enable_thinking`
  - `thinking_token_budget` when thinking is enabled and the budget is positive
- Ollama chat calls serialize compatible local-model options under `options.think` and `options.thinking_token_budget`.
- Blazor settings now persist and send VLM reasoning controls for image-backed VLM requests:
  - `vlm_enable_thinking`
  - `vlm_thinking_token_budget`
  - `vlm_filter_thinking_tokens`
- `vlm_filter_thinking_tokens` is currently request/contract synchronized; structured VLM `reasoning_content` streaming is covered in the follow-up section below.
- Added regression coverage for standard LLM thinking controls, VLM thinking request plumbing, and provider-level OpenAI-compatible/Ollama outbound JSON payloads.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 106 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Structured VLM Reasoning Streaming

- Added a provider-neutral `ChatStreamDelta` contract with separate `content` and `reasoning_content` channels.
- Extended `IChatCompletionService` with `StreamDeltasAsync` while keeping `StreamAsync` as the content-token compatibility path.
- OpenAI-compatible streaming now preserves both `delta.reasoning_content` and `delta.reasoning`, matching the Python VLM stream parser.
- Ollama streaming now preserves `message.reasoning_content` / `message.reasoning` when a local model emits those fields.
- `/generate` SSE now forwards buffered reasoning as `choices[0].delta.reasoning_content` before the final answer chunks, so the existing Blazor reasoning panel can display VLM reasoning output.
- VLM fallback clears failed reasoning/content buffers before retrying through the main LLM provider.
- Remaining streaming gap: .NET still buffers before emitting when guardrails/reflection are active; normal think-token filtering now streams visible content while removing inline `<think>...</think>` spans. Python streams from chain callbacks and can emit richer event-stage metadata during generation.
- Added regression coverage for:
  - OpenAI-compatible reasoning SSE parsing;
  - VLM `/generate` SSE carrying `reasoning_content`;
  - existing content-token streaming compatibility.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 108 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Query Decomposition Single-Query Fast Path

- Added the Python-equivalent `len(questions) == 1` branch for query decomposition.
- When the decomposition prompt returns one query, `.NET` now skips contextual rewrite, sub-query answer generation, and follow-up generation, retrieves/reranks once for the original query, and renders the final response prompt directly.
- This preserves the existing interface-based retrieval/reranking boundaries: the query-decomposition service still takes the active `IVectorStore`, optional reranker endpoint, and provider-specific filter expression from the RAG orchestration layer.
- Added regression coverage proving the direct path does not call iterative decomposition prompts and searches the selected collection only once.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 109 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET Direct SSE Streaming Fast Path

- Added a direct streaming path for normal `/generate` requests when buffering is not required:
  - request guardrails are disabled;
  - reflection is disabled;
  - `FILTER_THINK_TOKENS=false`.
- In that mode `.NET` now forwards provider `ChatStreamDelta` values as SSE events as they arrive instead of buffering the whole model response and replaying fixed-size chunks.
- The final SSE event still carries the shared citation envelope. Guarded/reflection paths keep the existing buffered behavior, while the think-token filtering path now streams visible content through a split-tag-aware filter.
- Added regression coverage proving separate provider deltas remain separate SSE content events and are not replayed as one buffered string.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 110 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## Full Python Source Inventory for .NET Parity

Inventory date: 2026-06-26. Scope inspected: every Python file returned by `rg --files src/nvidia_rag -g '*.py'` (67 files). Classification meanings:

- `migrated`: behavior or prompt source is represented in .NET with active usage.
- `copied-not-used`: copied or intentionally present as resource, but current .NET runtime does not execute it.
- `partial`: .NET has an equivalent boundary or subset, but meaningful behavior remains different.
- `missing`: no .NET runtime equivalent beyond explicit unavailability.
- `external-bridge-owned`: Python behavior is intentionally owned by NV-Ingest/NRL/external bridge until .NET gains native orchestration.
- `no-action-needed`: package/init/script/helper with no migration-relevant behavior.

### File Inventory

| Python source | Status | Inventory note |
|---|---:|---|
| `src/nvidia_rag/__init__.py` | no-action-needed | Package metadata only. |
| `src/nvidia_rag/ingestor_server/__init__.py` | no-action-needed | Package marker. |
| `src/nvidia_rag/ingestor_server/docker/scripts/post_build_triggers.py` | no-action-needed | Image build helper, not runtime parity surface. |
| `src/nvidia_rag/ingestor_server/health.py` | partial | Dependency health for NV-Ingest, Redis, object store, vector DB, NIM endpoints. .NET has health checks but less Python-specific dependency detail. |
| `src/nvidia_rag/ingestor_server/ingestion_state_manager.py` | partial | Ingestion state manager. .NET has durable task store/queue, but not same state schema internals. |
| `src/nvidia_rag/ingestor_server/main.py` | partial | Python ingestor orchestration, collection/document lifecycle, auto-create, metadata/document-info, NV-Ingest/NRL selection, summary, object-store cleanup. .NET has broad parity but still lacks native NV-Ingest/NRL execution semantics. |
| `src/nvidia_rag/ingestor_server/nemo_retriever/__init__.py` | external-bridge-owned | Lazy NRL package public surface. .NET uses external bridge boundary for NRL. |
| `src/nvidia_rag/ingestor_server/nemo_retriever/extensions.py` | external-bridge-owned | NRL extension/type vocabulary. .NET has local supported-extension checks but no native NRL graph extraction. |
| `src/nvidia_rag/ingestor_server/nemo_retriever/filters.py` | partial | NRL-supported file filtering. .NET has unsupported-extension handling but not full NRL type matrix. |
| `src/nvidia_rag/ingestor_server/nemo_retriever/handler.py` | external-bridge-owned | Async facade over synchronous NRL `GraphIngestor`. .NET calls bridge, no native handler. |
| `src/nvidia_rag/ingestor_server/nemo_retriever/ingest_schema_manager.py` | external-bridge-owned | NRL result DataFrame schema isolation. .NET bridge unpacks generic response only. |
| `src/nvidia_rag/ingestor_server/nemo_retriever/params.py` | external-bridge-owned | Python config to NRL Pydantic param mapping. .NET does not construct native NRL params. |
| `src/nvidia_rag/ingestor_server/nvingest.py` | external-bridge-owned | NV-Ingest client pipeline: extract, split, caption, embed, save, store assets, VDB upload. .NET uses local pipeline or external bridge. |
| `src/nvidia_rag/ingestor_server/server.py` | partial | FastAPI ingestion contracts, multipart parsing, bridge `/bridge/extract`, real-backend flags. .NET ingestor has corresponding contracts and bridge client, but not exact Python bridge backend execution. |
| `src/nvidia_rag/ingestor_server/task_handler.py` | partial | Redis-backed task handling/status. .NET has file-backed durable queue/store, not Redis contract. |
| `src/nvidia_rag/rag_server/__init__.py` | no-action-needed | Package marker. |
| `src/nvidia_rag/rag_server/agentic_rag/__init__.py` | partial | Agentic RAG public package. .NET exposes Agentic only behind `ENABLE_AGENTIC_RAG=true`; disabled deployments still return 501 and hide the Blazor selector. |
| `src/nvidia_rag/rag_server/agentic_rag/agentic_rag.py` | partial | Python LangGraph plan/execute/synthesis/verification pipeline, role LLM overrides, stream-aware retries, agent metrics. .NET intentionally uses a custom .NET-native service workflow (`IAgenticRagService`, `IAgenticOrchestrationService`, `IAgenticPlannerService`, `IAgenticRoleService`) with bounded follow-up execution, provider-neutral citations, and gated API handling; exact Python graph behavior remains. |
| `src/nvidia_rag/rag_server/agentic_rag/builder.py` | partial | Agentic retriever bridge, per-request contextvars for search/LLM overrides, citation accumulation. .NET retrieves through `IVectorStore`, collates citations per task, honors role model defaults, and now creates request-local Agentic planner/role/vector services through `IChatCompletionClientFactory` and `IVectorStoreClientFactory` when Agentic requests include `llm_endpoint`, model, `vdb_endpoint`, bearer auth, or embedding overrides. Exact Python contextvar graph behavior remains broader. |
| `src/nvidia_rag/rag_server/agentic_rag/prompt.py` | migrated | Embedded agentic prompts are centralized as first-class .NET `PromptCatalog.Agentic` sections and consumed by planner/task/seed/synthesis/verification services. |
| `src/nvidia_rag/rag_server/agentic_rag/response_parser.py` | migrated | Robust JSON recovery for agentic planner/task/verification outputs. .NET `AgenticResponseParser` covers direct JSON, false-start/restart output, missing-colon array/object typo recovery, typed plan/task/seed/synthesis/verification parsing, and parse-error reporting. |
| `src/nvidia_rag/rag_server/agentic_rag/runner.py` | partial | Agentic request runner, streaming/non-streaming orchestration, citation collation. .NET `FeatureFlaggedAgenticRagService` and `IAgenticOrchestrationService` cover gated non-streaming, SSE, multi-task execution, follow-up execution, and citation payloads; exact Python runner graph behavior remains. |
| `src/nvidia_rag/rag_server/agentic_rag/streaming.py` | partial | Python LangGraph event translator to ChainResponse SSE with stage events/reasoning/citations. .NET streaming flushes live events from its own orchestration event sink plus synthesis provider deltas, then final citations; exact Python event names/timing need live fixture comparison. |
| `src/nvidia_rag/rag_server/agentic_rag/tracing.py` | partial | Agentic per-query trace and aggregate metrics. .NET tags orchestration spans, planner/role token usage, emits namespaced Agentic request/error/task/follow-up/citation counters plus orchestration duration, and now also emits Python-compatible `agentic_*` request/duration/stage/plan/scope/verification/retrieval/task/LLM/error metric aliases from the native orchestrator, planner, and role service. Exact aggregate rollups and live timing parity remain. |
| `src/nvidia_rag/rag_server/health.py` | partial | RAG dependency health, object store, NIM endpoints. .NET health exists but differs in dependency coverage/details. |
| `src/nvidia_rag/rag_server/main.py` | partial | Core RAG orchestrator: runtime overrides, VLM, citations, reflection, filters, decomposition, agentic, metrics, streaming. .NET implements many paths but still has remaining gaps below. |
| `src/nvidia_rag/rag_server/query_decomposition.py` | partial | YAML-backed iterative decomposition, single-query fast path, score normalization, final response, LangChain callbacks. .NET now has most logic but differs in exact callback/stream and some iterative controls. |
| `src/nvidia_rag/rag_server/reflection.py` | migrated | Reflection prompts and deterministic role LLM behavior migrated to .NET service; telemetry/counters remain less detailed. |
| `src/nvidia_rag/rag_server/response_generator.py` | partial | RAGResponse/ChainResponse models, async streaming, usage, citations, NRL citations, summary retrieval. .NET has compatible response surface but lacks multimodal asset citation parity. |
| `src/nvidia_rag/rag_server/server.py` | partial | FastAPI contracts/routes/configuration/OpenAI vector-store search. .NET has analogous contracts/routes, but not every validation/callback/detail. |
| `src/nvidia_rag/rag_server/validation.py` | partial | Python validation helpers and standard error messages. .NET has core validation, not complete message/schema parity. |
| `src/nvidia_rag/rag_server/vlm.py` | partial | VLM prompt assembly, multimodal context/image handling, reasoning streaming, image ordering/page grouping. .NET has VLM prompt, reasoning deltas, retrieved KB text context, resolved visual assets, and source/page-ordered visual context grouping; full NV-Ingest/NRL asset orchestration remains external. |
| `src/nvidia_rag/utils/__init__.py` | no-action-needed | Package marker. |
| `src/nvidia_rag/utils/agentic_rag_config.py` | partial | Agentic config schema. .NET maps core Agentic planner/task/seed/synthesis/verification model and retry limits through `RagServerConfiguration`; exact Python config surface remains broader. |
| `src/nvidia_rag/utils/batch_utils.py` | partial | Dynamic ingestion batch sizing. .NET local ingestion uses simpler batching. |
| `src/nvidia_rag/utils/common.py` | partial | Shared confidence filtering, metadata config, filter validation/processing, catalog/document metadata, aggregation. .NET has many equivalents but not all schema/filter/document-info semantics. |
| `src/nvidia_rag/utils/configuration.py` | partial | Full Pydantic/env configuration surface. .NET maps many env vars but not every Python/NV-Ingest/NRL/agentic option. |
| `src/nvidia_rag/utils/embedding.py` | migrated | Embedding provider abstraction represented in .NET with Ollama/OpenAI-compatible providers and request-scoped overrides. |
| `src/nvidia_rag/utils/es_filter_validator.py` | copied-not-used | Elasticsearch filter validator. .NET scope is ChromaDB/Milvus; ES prompt/validator copied conceptually but unused. |
| `src/nvidia_rag/utils/filter_expression_generator.py` | partial | Prompt-driven generated filters for Milvus strings and ES JSON. .NET supports Milvus generation and Chroma explicit filters; ES not active. |
| `src/nvidia_rag/utils/health_models.py` | partial | Pydantic health response models. .NET health response shape overlaps but is not exact. |
| `src/nvidia_rag/utils/llm.py` | partial | Prompt YAML loading, NVIDIA/OpenAI LangChain LLM construction, reasoning config, think-token parsers, usage capture. .NET has prompt catalog and provider clients, but not full LangChain parser/callback behavior. |
| `src/nvidia_rag/utils/metadata_validation.py` | partial | Metadata schema grammar, validation, Milvus query transform. .NET has schema vocabulary and simple Chroma/Milvus filters but not full grammar parity. |
| `src/nvidia_rag/utils/object_store.py` | partial | S3/filesystem object store and thumbnail/asset ID helpers. .NET has filesystem object store, but citation asset lookup/thumbnail ID parity remains incomplete. |
| `src/nvidia_rag/utils/observability/agentic_metrics.py` | partial | Agentic metrics. .NET has Agentic orchestration span tags, planner/role token-usage spans, namespaced Agentic counters/duration histogram, and Python-compatible `agentic_*` request/duration/stage/plan/scope/verification/retrieval/task/LLM/error metric aliases. Exact aggregate rollups and live timing parity remain. |
| `src/nvidia_rag/utils/observability/langchain_callback_handler.py` | partial | LangChain spans, prompt capture, request/response attributes. .NET has OTel/tracing fixtures but no LangChain callback equivalent. |
| `src/nvidia_rag/utils/observability/langchain_instrumentor.py` | copied-not-used | Python LangChain instrumentation. Not applicable to .NET except conceptual tracing. |
| `src/nvidia_rag/utils/observability/otel_metrics.py` | partial | Python OTel metrics. .NET has metrics/OTLP trace fixture, not all metric families. |
| `src/nvidia_rag/utils/observability/tracing/__init__.py` | no-action-needed | Tracing helper exports. |
| `src/nvidia_rag/utils/observability/tracing/helpers.py` | partial | Span helpers, usage collector scope, NV-Ingest trace processing. .NET has direct OTel spans but no equivalent usage collector/NV-Ingest trace processing. |
| `src/nvidia_rag/utils/observability/tracing/instrumentation.py` | partial | FastAPI/HTTP instrumentation and span filtering. .NET has ASP.NET/OTel wiring, not same filters. |
| `src/nvidia_rag/utils/reranker.py` | partial | NVIDIA text/VLM reranker factory. .NET has `IRerankerClient` HTTP/local fallback, but no native VLM reranker request assembly. |
| `src/nvidia_rag/utils/summarization.py` | partial | Redis-coordinated parallel summaries, prompt chains, object-store status, page filters. .NET prompt catalog and summary services exist, but not full Redis/parallel status semantics. |
| `src/nvidia_rag/utils/summary_status_handler.py` | partial | Redis summary status tracking. .NET has file/object-store summary retrieval but not Redis status contract. |
| `src/nvidia_rag/utils/vdb/__init__.py` | partial | Provider factory for Elasticsearch/Milvus/LanceDB. .NET provider factory supports ChromaDB/Milvus only. |
| `src/nvidia_rag/utils/vdb/elasticsearch/__init__.py` | copied-not-used | Elasticsearch package marker. .NET not targeting ES. |
| `src/nvidia_rag/utils/vdb/elasticsearch/elastic_vdb.py` | copied-not-used | Elasticsearch full VDB backend. .NET not targeting ES. |
| `src/nvidia_rag/utils/vdb/elasticsearch/es_dense_vector_strategy.py` | copied-not-used | ES dense-vector strategy. .NET not targeting ES. |
| `src/nvidia_rag/utils/vdb/elasticsearch/es_queries.py` | copied-not-used | ES metadata/document-info query helpers. .NET not targeting ES. |
| `src/nvidia_rag/utils/vdb/lancedb/__init_.py` | external-bridge-owned | LanceDB package marker for NRL mode. .NET does not implement LanceDB/NRL natively. |
| `src/nvidia_rag/utils/vdb/lancedb/lancedb_vdb.py` | external-bridge-owned | LanceDB VDB for NRL ingestion/RAG. .NET bridge boundary only. |
| `src/nvidia_rag/utils/vdb/lancedb/nrl_lancedb.py` | external-bridge-owned | NRL-aware LanceDB LangChain wrapper and metadata repr parsing. .NET bridge boundary only. |
| `src/nvidia_rag/utils/vdb/milvus/__init__.py` | partial | Milvus package marker. .NET has Milvus REST provider. |
| `src/nvidia_rag/utils/vdb/milvus/milvus_vdb.py` | partial | Milvus schema, metadata schema collection, document-info, compaction, retrieval. .NET implements major REST/search/lifecycle adaptation, but not every metadata/document-info/compaction behavior. |
| `src/nvidia_rag/utils/vdb/vdb_base.py` | migrated | Abstract RAG VDB operations map to .NET `IVectorStore`/management abstractions. |
| `src/nvidia_rag/utils/vdb/vdb_ingest_base.py` | partial | VDB+NV-Ingest write wrapper and serialized writes. .NET has provider abstractions and queue but not NV-Ingest VDB upload path. |
| `src/nvidia_rag/utils/vlm_reranker.py` | missing | Multimodal reranker request assembly. .NET reranker client is text-oriented/local and does not build image passage payloads. |

### Detailed Partial/Missing Gap Register

| Priority | Python file/path | Behavior or prompt found | Current .NET equivalent | Remaining gap | Recommended next implementation slice |
|---:|---|---|---|---|---|
| P0 | `rag_server/agentic_rag/*.py`, `utils/agentic_rag_config.py` | Python LangGraph agentic pipeline, planner/task/seed/synthesis/verification prompts, robust JSON parser, per-role LLM overrides, graph streaming, agentic metrics/citations. | Prompt resources copied into `PromptCatalog.Agentic`; the custom .NET-native Agentic workflow is composed from `IAgenticRagService`, `IAgenticOrchestrationService`, `IAgenticPlannerService`, and `IAgenticRoleService`; `AgenticResponseParser` ports Python's robust JSON recovery and typed planner/task/seed/synthesis/verification parsing; internal services render prompts, retrieve through `IVectorStore`, perform scope discovery/replanning, execute all planned tasks, retry partial task answers through seed-query generation, synthesize, verify, execute bounded verification follow-up tasks, collate provider-neutral citations, build request-local Agentic services through vector/chat provider factories for runtime overrides, emit Agentic counters/duration metrics including planner LLM usage and scope-round aliases, map non-streaming OpenAI-compatible responses, emit frontend-compatible SSE with live stage events plus synthesis provider deltas, and expose the Blazor Agentic selector only when `/configuration` reports `enable_agentic_rag=true`. | Exact Python graph behavior, event semantics/timing, and metric-family parity are not fully proven. The .NET target remains a native orchestrator, not LangGraph. | Next slice needs runtime fixtures before claiming exact Python Agentic graph parity. |
| P0 | `rag_server/response_generator.py`, `utils/object_store.py`, `ingestor_server/nvingest.py` | Multimodal citations fetch image/table/chart bytes from object store, base64 encode content, populate source metadata/location/page. NV-Ingest `.store()` creates those assets. | .NET has citation envelope, filesystem object store, local ingestion artifacts, an `ICitationAssetResolver` that enriches visual citation content from explicit asset URI metadata, raw bridge `asset_object_names`, Python-style nested `source` / `content_metadata` JSON, object-valued asset arrays, inline data URIs, and thumbnail aliases; Blazor renders image/table/chart citation payloads; citation payloads expose Python-style `metadata`; ingestion propagates bridge visual asset metadata into vector metadata; deterministic regression covers bridge/nested metadata -> retrieval -> citation asset resolution and VLM context injection. | Local ingestion still does not create NV-Ingest-style image/table/chart assets or Python thumbnail IDs; bridge must provide asset URI metadata or inline data. | Add live multi-process fixture only after a shared RAG+ingestor fixture orchestrator exists. |
| P0 | `ingestor_server/nvingest.py`, `ingestor_server/nemo_retriever/*.py`, `utils/vdb/lancedb/*.py` | Native NV-Ingest and NRL graph extraction/caption/embed/store/VDB upload. | .NET `IIngestionPipeline` supports local/external bridge; Python bridge can call real backend with flags. | Native runtime still external; full Python baseline blocked by NV-Ingest availability. | Keep external-bridge-owned; next local slice should only harden bridge response schema and object-store artifact propagation. |
| P1 | `rag_server/main.py`, `rag_server/response_generator.py`, `utils/llm.py` | LangChain/ChainResponse streaming, usage callbacks, async think-token split/filter, reflection-aware regeneration, final metadata. | .NET has direct SSE fast path, split-tag-aware streaming think-token filtering, OpenAI-compatible stream usage propagation, and buffered guarded/reflection paths. | Guardrails/reflection still buffer by design; Python aggregate usage/final event metadata remains richer. | Add live Python streaming fixture comparison for guarded/reflection paths before changing semantics further. |
| P1 | `rag_server/vlm.py` | VLM message assembly with retrieved docs, image/page organization, max-total-images, reasoning delta forwarding, VLM citations instruction prompt. | .NET VLM direct route uses YAML system prompt, multimodal user content, structured reasoning deltas, and an `IVlmContextAssembler` that injects retrieved KB text plus resolved visual assets from `ICitationAssetResolver` into the VLM context message. User images are preserved, retrieved images consume the remaining image budget, and resolved visual assets are grouped in source/page order when page metadata is available. | Remaining differences are NV-Ingest/NRL thumbnail ID behavior, live object-store asset fixtures, and exact citations-instruction wording; `vlm_filter_thinking_tokens` is request-synced but not behaviorally meaningful beyond separate reasoning channel. | Add live page/image fixture comparison once NV-Ingest/NRL visual asset fixture orchestration is available; keep VLM asset lookup behind resolver interfaces. |
| P1 | `rag_server/query_decomposition.py` | Iterative decomposition, single-query fast path, follow-ups, score normalization, final prompt, LangChain callbacks. | .NET has YAML prompts, single-query fast path, follow-ups, merged retrieval, score normalization, Python conversation-history separator coverage, and final prompt rendering coverage for empty-history/single-query and multi-query edge cases. | Remaining differences are LangChain callback mechanics and Python's specific streaming final-response generator behavior. | Add runtime fixture comparison once Python and .NET query-decomposition fixture orchestration is available. |
| P1 | `utils/metadata_validation.py`, `utils/common.py`, `utils/filter_expression_generator.py`, `utils/es_filter_validator.py` | Metadata grammar, Milvus transform, ES DSL validation, generated filter prompt behavior and field eligibility. | .NET has `IVectorStoreFilterCapabilities`, Milvus generated filters, and concrete Chroma explicit filter translation for nested `AND`/`OR`, ranges, `in`/`not in`, booleans/numbers, and single/double-quoted metadata selectors. | Full Python grammar and ES DSL are intentionally not implemented for Chroma/Milvus-only parity. | Keep extending concrete provider parsers only when a supported vector DB needs another filter shape. |
| P1 | `utils/vdb/milvus/milvus_vdb.py` | Python/NV-Ingest Milvus schema, metadata schema/doc-info collections, compaction and delete/update semantics. | .NET Milvus REST provider adapts actual fields and supports lifecycle/search/delete; document-name listing is covered for .NET metadata, Python/NV-Ingest source fields, and id-prefix fallback; `IVectorStoreManagement.ListCollectionsAsync` now aggregates normal collection names, row counts, Python `metadata_schema`, and Python `document_info` catalog/collection entries. | Some delete/update semantics and compaction parity remains incomplete. | Add live Milvus management fixtures when Python/NV-Ingest system collections are available. |
| P1 | `ingestor_server/main.py`, `ingestor_server/server.py`, `utils/summarization.py`, `utils/summary_status_handler.py` | Summary generation with page filters, Redis status, object-store summaries, blocking/non-blocking retrieval. | .NET summary generation/retrieval through provider interfaces and object store; in-process summary progress tracker now carries Python-style terminal completion timestamps. | Redis cross-service persistence and parallel coordination semantics still differ. | Add a pluggable summary status store interface when Redis/file-backed cross-process status becomes required. |
| P2 | `utils/reranker.py`, `utils/vlm_reranker.py` | Text reranker plus VLM reranker for image passages. | .NET `IRerankerClient` supports local/HTTP text reranking. | No VLM reranker multimodal payload support. | Extend reranker interface only after VLM image citation assets are available. |
| P2 | `utils/observability/*.py`, `rag_server/agentic_rag/tracing.py` | LangChain callbacks, OTel metrics, span filtering, usage collection, agentic metrics. | .NET has OTel trace fixture, basic metrics, generation spans tagged with prompt template/model/KB context, Python-compatible `gen_ai.usage.*` token usage, and role-stage spans for query rewriting, filter generation, query decomposition, and reflection. | Remaining differences are aggregate per-feature usage scope rollups, exact metric family names, span filtering details, and agentic metrics. | Add aggregate usage rollup spans after agentic/runtime behavior stabilizes. |
| P2 | `ingestor_server/health.py`, `rag_server/health.py`, `utils/health_models.py` | Detailed dependency health shape for NIM/NV-Ingest/Redis/object store/vector DB. | .NET health endpoint exists and tests pass. | Response detail differs; external dependencies differ by deployment mode. | Add health response contract tests for selected dependency modes only. |
| P3 | `utils/batch_utils.py`, `ingestor_server/ingestion_state_manager.py`, `ingestor_server/task_handler.py` | Dynamic batching, task state, Redis tasks. | .NET queue/task store is durable and file-backed. | Runtime semantics differ but local fixtures pass. | Defer until NV-Ingest/Redis runtime is available. |

### Reconciled Active .NET Plan

- Agentic RAG:
  - .NET-specific implementation decision: do not use LangGraph. The current runtime is a custom .NET-native orchestrator made of `FeatureFlaggedAgenticRagService`, `AgenticOrchestrationService`, `AgenticPlannerService`, `AgenticRoleService`, and `AgenticResponseParser`, wired through interfaces and provider abstractions.
  - Current decision: allow backend and Blazor Agentic only behind explicit `ENABLE_AGENTIC_RAG=true`; keep disabled deployments on Standard mode.
  - Interface seam is now present via `IAgenticRagService`; `AgenticRagInvocation` defines the stable runtime input contract without changing default user-visible availability.
  - `AgenticResponseParser` now matches Python's robust JSON response recovery patterns for direct JSON, false-start/restart output, missing-colon array/object typos, and parse-error reporting.
  - Typed `AgenticPlan`/`AgenticPlanTask` parsing validates required planner task fields (`id`, `question`, `query`) without changing runtime availability.
  - Internal `IAgenticPlannerService` now renders `PromptCatalog.Agentic.PlannerPrompt`, applies Python-compatible planner defaults/envs (`AGENTIC_PLANNER_MAX_TASKS`, scope rounds, attempts, planner LLM knobs), retries malformed planner output, trims tasks to the configured maximum, and returns typed plans.
  - Internal `IAgenticRoleService` now renders task-answer, seed-generation, synthesis, and verification prompts; parses typed task/seed/synthesis/verification outputs; and honors Python-style per-role model env fallbacks.
  - Hidden `IAgenticOrchestrationService` now proves planner -> vector retrieval through `IVectorStore` -> scope discovery/replanning -> one task answer with Python-style seed-query retries for partial answers -> synthesis -> verification composition, including one bounded verification follow-up task round, resynthesis, and provider-neutral citation collation from retrieved task documents.
  - Agentic request-scoped dependency overrides now use the same interface-driven provider boundaries as standard RAG: `vdb_endpoint`, bearer auth, `embedding_endpoint`, `embedding_model`, `llm_endpoint`, and request `model` build a request-local native Agentic orchestrator through `IVectorStoreClientFactory` and `IChatCompletionClientFactory`; default Agentic requests still use the singleton orchestrator.
  - `FeatureFlaggedAgenticRagService` now maps non-streaming OpenAI-compatible Agentic responses and frontend-compatible SSE responses with live stage events plus streamed synthesis deltas behind `ENABLE_AGENTIC_RAG=true`; disabled mode still returns 501.
  - `RagMetrics` now exports Agentic request/error/task/follow-up/citation counters and orchestration duration from the custom .NET-native orchestrator, plus Python-compatible `agentic_*` metric aliases for request count, request duration in milliseconds, stage duration in milliseconds, LLM calls/duration/tokens including planner calls, planned tasks, scope discovery rounds, retrieval calls/chunks, task outcomes/attempts, verification outcomes, follow-up task counts, and errors.
  - `RagService` has unit coverage proving Agentic requests delegate through `IAgenticRagService`, so fallback/unavailable behavior and the enabled .NET-native runtime remain swappable behind the interface.
  - Blazor now reads `enable_agentic_rag` from `/configuration` and shows the Agentic selector option only when the backend has explicitly enabled it.
- True streaming parity:
  - Current state: direct SSE fast path exists when guardrails/reflection are off; structured VLM reasoning deltas are preserved; inline `<think>...</think>` content is now filtered in a streaming path instead of forcing full-response buffering.
  - OpenAI-compatible streaming now requests `stream_options.include_usage`, parses usage-only chunks, and includes usage in final SSE events when the provider emits it.
  - Generation spans now include prompt template/model/KB context tags and `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, and `llm.usage.total_tokens` when usage metadata is available.
  - Query rewriting now emits a `rag.Query Rewriting.token_usage` span with prompt/model/history and usage tags.
  - Filter generation now emits a `rag.Custom Metadata.token_usage` span with prompt/model/provider/collection and usage tags.
  - Query decomposition now emits `rag.Query Decomposition.token_usage` spans for LLM sub-steps with step/prompt/model and usage tags.
  - Reflection now emits `rag.Self Reflection.*.token_usage` spans for context relevance, groundedness, query rewrite, and regeneration calls.
  - Remaining: guarded/reflection paths still buffer by necessity; Python aggregate usage rollup spans and exact metric families remain richer.
  - Next local slice: live Python streaming fixture comparison for guarded/reflection behavior, or aggregate metric rollup spans if a Python metrics baseline is available.
- VLM/image citation asset parity:
  - Current state: VLM requests and reasoning controls are synced; citation envelopes exist; `ICitationAssetResolver` enriches image/table/chart citation content from `source_location`, `stored_image_uri`, nested `source` JSON, object-valued `asset_object_names`, thumbnail aliases, or inline data URI metadata when bytes can be resolved. `IVlmContextAssembler` now injects retrieved KB text context and resolved visual assets for VLM+KB requests through the shared prompt catalog.
  - Blazor now deserializes citation `text` and renders image/table/chart citation `content` as an image when content is base64 or a data URI, while preserving caption/text below the asset.
  - Citation payloads now include a Python-style `metadata` object with page number, description, location, source location, and recovered `content_metadata` when vector metadata provides those fields.
  - External ingestion bridge metadata now flows into vector chunk metadata for document type, page number, source/asset URI, and flattened `content_metadata.*` fields.
  - Deterministic regression now covers bridge-style and nested Python/NV-Ingest visual asset metadata flowing through vector metadata, retrieval result, citation asset resolver, final citation payload, and VLM context asset injection.
  - Resolved visual context is now grouped in source/page order when page metadata is available, matching Python's `organize_by_page` ordering strategy for the assets .NET can already resolve.
  - Remaining: Python's ingestion pipeline creates NV-Ingest visual artifacts and thumbnail IDs; .NET local ingestion does not create equivalent image/table/chart assets by itself. VLM prompt assembly still has exact citations-instruction wording differences.
  - Next local slice: add a deterministic RAG/VLM fixture with filesystem visual assets if a shared RAG fixture runner can mount an object-store root; otherwise live NV-Ingest/NRL visual asset fixtures are the unblocker.
- Query decomposition exact parity:
  - Current state: YAML prompts, iterative flow, follow-up handling, score normalization, and single-query fast path implemented.
  - Parser behavior now matches Python's prefixed-line preference: numbered/bulleted subqueries are preferred over preamble/trailing prose, while a single unprefixed line is still accepted.
  - Conversation history formatting is covered against Python's `Question: ...\nAnswer: ...\n\n\n...` separator, including empty history.
  - Query decomposition LLM sub-steps now emit `rag.Query Decomposition.token_usage` spans with usage tags.
  - Remaining: final response/citation generator differences and aggregate usage scopes.
  - Next local slice: reflection stage spans.
- ChromaDB and Milvus compatibility:
  - Current state: interface-selected providers, Milvus generated filters, Chroma explicit filters, Milvus schema adaptation.
  - Chroma explicit filters now support `AND`, top-level `OR`, ranges, equality/inequality, `IN`, and `NOT IN` in provider-owned translation.
  - Milvus document listing is covered across .NET metadata fields, Python/NV-Ingest `source.source_id`, and legacy chunk-id fallback.
  - Remaining: full metadata grammar and Python metadata-schema collection aggregation.
  - Next local slice: add summary status abstraction/status-transition parity tests.
- Blazor UI sync:
  - Current state: query decomposition, role dependency settings, VLM thinking controls, agentic hidden, citations handled.
  - Remaining: image citation asset rendering may require richer citation fields once backend resolves assets.
  - Next local slice: after citation asset resolver, ensure Blazor distinguishes text/image/table/chart citation content cleanly.
- Interface-based provider design:
  - Keep vector DB, object store, chat, embedding, reranker, ingestion, summarization, and future agentic behavior behind interfaces.
  - Concrete ChromaDB/Milvus/ObjectStore implementations should own technology-specific filtering, schema, and asset lookup behavior.

## .NET Agentic RAG Interface Boundary

- Added `IAgenticRagService` as the explicit backend seam for future Agentic RAG runtime work.
- Added `UnavailableAgenticRagService`, preserving current behavior:
  - per-request `agentic=true` returns HTTP 501;
  - server-wide `ENABLE_AGENTIC_RAG=true` also routes to the unavailable service;
  - normal LLM generation is not invoked for unavailable agentic requests.
- `RagService` now delegates agentic request detection/handling to the interface instead of embedding hard-coded 501 logic in the main generation path.
- Registered the unavailable implementation in RAG server DI.
- Superseded by later slices: production DI now registers `FeatureFlaggedAgenticRagService` and the custom .NET-native Agentic runtime; `UnavailableAgenticRagService` remains only as a fallback/test implementation for deployments without an enabled Agentic runtime.
- Added regression coverage for both request-scoped and config-enabled agentic unavailability.
- Validation after this change:
  - `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 111 tests.
  - `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
  - `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
  - `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

## .NET VLM/Image Citation Asset Resolver

- Added `ICitationAssetResolver` as the RAG citation asset boundary.
- Added `FileSystemCitationAssetResolver`, registered in RAG server DI:
  - resolves visual citation assets for `image`, `table`, `chart`, `structured`, and `image_caption` metadata;
  - supports `file://` URIs, absolute paths, `APP_OBJECT_STORE_ROOT` relative paths, `s3://bucket/key` mapped under `APP_OBJECT_STORE_ROOT`, and raw bridge `asset_object_names` JSON-array or delimited-string fallbacks;
  - returns base64 content for the citation `content` field while preserving retrieved caption/text in the citation `text` field.
- `RagService` citation payload construction is now async and resolver-backed for both non-streaming responses and final SSE citation events.
- Text citations retain the existing payload shape.
- Added unit coverage for a visual citation using resolved base64 asset content.

## .NET Blazor Visual Citation Rendering

- Blazor `Citation` now deserializes the backend `text` field separately from `content`.
- `CitationsPanel` now renders visual citation types (`image`, `table`, `chart`, `structured`, `image_caption`) as images when `content` is a base64 payload or `data:image/*` URI.
- Visual citation captions use the preserved citation `text` field; text citations continue to render `content` as before.
- Added panel styling for bounded visual assets so citation images fit inside the side panel without layout expansion.

## .NET Citation Provenance Metadata

- `RagService` citation payloads now include a Python-style nested `metadata` object.
- Preserved fields include:
  - `page_number`, `description`, `location`, `location_max_dimensions`, `height`, `width`, source timestamps/language when present;
  - `source_location` from `source_location`, `stored_image_uri`, `storage_uri`, `image_uri`, or `asset_uri`;
  - recovered `content_metadata.*` entries from vector-store metadata.
- Blazor citation models now deserialize citation `metadata` and display page numbers in the citation panel.
- Added regression coverage for page/location/content metadata in non-streaming citation payloads.

## .NET Ingestion Visual Metadata Propagation

- `IngestorService` now builds vector metadata through a shared helper that preserves bridge-provided visual citation fields.
- Propagated fields include:
  - `document_type` as vector metadata `type`;
  - `page_number`;
  - `source_location`, `stored_image_uri`, `image_uri`, and `asset_uri`;
  - first `asset_object_names` entry as both `source_location` and `stored_image_uri` when no explicit source URI is present;
  - nested `content_metadata` flattened as `content_metadata.*`.
- Added unit coverage proving bridge visual asset metadata reaches vector metadata in provider-neutral form.
- Added deterministic cross-component regression coverage for bridge-style visual asset metadata through vector metadata, retrieval result, citation asset resolver, and final citation payload.

## .NET Streaming Usage Metadata

- OpenAI-compatible streaming payloads now request `stream_options.include_usage`.
- `ChatStreamDelta` carries optional usage metadata.
- OpenAI-compatible SSE parsing now handles usage-only chunks with empty `choices`.
- RAG final SSE events include `usage` when the provider emits streaming usage.
- VLM streaming and fallback streaming share the same final SSE writer and usage propagation path.
- Added provider and RAG service regression coverage for streaming usage metadata.

## .NET Query Decomposition Exact Parsing Fixtures

- `QueryDecompositionService.ParseSubqueries` now follows Python behavior:
  - numbered and bulleted lines are preferred when present;
  - surrounding prose is ignored when a prefixed list exists;
  - unprefixed single-line responses still become a single subquery.
- `QueryDecompositionService.FormatConversationHistory` is exposed for fixture coverage and matches Python's triple-newline separator.
- Added focused unit coverage for mixed prose/list parsing, bullet parsing, single-line fallback, populated history formatting, and empty history formatting.

## .NET Chroma Filter Compatibility

- Chroma provider-owned explicit filter translation now handles:
  - top-level `OR` expressions;
  - nested `AND` expressions through recursive translation;
  - `IN` and `NOT IN` array filters;
  - existing equality, inequality, and range filters.
- RAG orchestration remains provider-neutral; the Chroma concrete provider owns its filter syntax conversion.
- Added unit coverage for `IN`, `NOT IN`, and `OR` translation payloads.

## .NET Milvus Document Listing Compatibility

- Added Milvus management regression coverage for document-name listing across:
  - .NET compatibility schema using `metadata.filename`;
  - Python/NV-Ingest schema using `source.source_id`;
  - minimal legacy schema using `id` chunk prefixes.
- This locks the provider-owned schema adaptation needed by ingestion update/delete and catalog workflows.

## .NET Summary Status Transitions

- `SummaryProgressTracker` now records `CompletedAt` for terminal `SUCCESS` and `FAILED` states.
- `IN_PROGRESS` states retain progress details and do not carry completion timestamps.
- Terminal updates preserve the original `StartedAt` timestamp.
- Added unit coverage for `IN_PROGRESS`, `SUCCESS`, and `FAILED` transitions.
- Added `ISummaryProgressStore` with in-memory and file-backed implementations.
- `APP_SUMMARY_STATUS_STORE_PATH` enables cross-process summary status persistence without changing the `SummaryProgressTracker` call sites.
- Added unit coverage proving file-backed status survives new tracker/store instances.

## .NET Agentic RAG Invocation Contract

- Added `AgenticRagInvocation` as the structured input contract for future Agentic RAG runtime work.
- `RagService` now passes the invocation to `IAgenticRagService` instead of raw request/prompt arguments.
- The unavailable implementation still returns HTTP 501, preserving current behavior and hidden Blazor availability.
- Added unit coverage for request path, streaming mode, user query, and collection-name capture.

## Python/.NET RAG Mock Parity Matrix

- Added `fixtures/run_rag_mock_parity_matrix.py`.
- The runner seeds the shared `parity_mock_data` Milvus collection, starts the deterministic OpenAI-compatible mock endpoint, launches Python RAG and .NET RAG, and executes the same fixture IDs against both runtimes.
- Local result on June 26, 2026:
  - `RAG-HEALTH-001`: pass on Python and .NET.
  - `RAG-GEN-MOCK-001`: pass on Python and .NET.
  - `RAG-SRCH-MOCK-001`: pass on Python and .NET.
  - `RAG-GEN-QD-MOCK-001`: pass on Python and .NET with `enable_query_decomposition=true`.
  - `RAG-GEN-QD-SINGLE-MOCK-001`: pass on Python and .NET for the single-query decomposition fast path.
- The matrix exposed a concrete .NET Milvus provider gap: Python-schema Milvus search can return nested hit arrays and/or rows without a nested `entity` object. `MilvusVectorStore.SearchAsync` now parses both shapes, converts numeric IDs to strings, and preserves Python `source` / `content_metadata` fields.
- Added unit coverage for direct and nested Milvus search rows.
- Registered the main chat completion service under keyed DI name `"main"` so `ENABLE_AGENTIC_RAG=true` can resolve the Agentic planner/role services.
- The matrix also exposed an explicit override mismatch: .NET treated `confidence_threshold: 0.0` as absent and fell back to the configured default. `Prompt` and `DocumentSearch` now use nullable thresholds so omitted values use config defaults while explicit `0.0` preserves Python no-filter semantics.
- Added `stream_delta_contains_all` fixture assertions that reconstruct OpenAI-compatible SSE content and verify deterministic answer text, so the mock matrix now checks response content instead of only envelope shape.
- Added `RAG-GEN-AGENTIC-MOCK-001`, a deterministic Agentic RAG smoke fixture. The mock LLM returns planner JSON, task-answer JSON, synthesis text, and verification JSON; both Python and .NET passed against the seeded Milvus collection.
- .NET Agentic orchestration now executes every planner task with `Task.WhenAll` instead of only the first task. Unit coverage verifies multi-task answers are passed into synthesis and citations retain the originating task IDs.
- .NET Agentic streaming now emits Python-style stage chunks with `event_type`, `stage`, and `reasoning_content` for plan, execute, synthesize, and verify stages. Unit and fixture coverage assert these fields.
- .NET Agentic metrics now emit native OpenTelemetry instruments for Agentic requests, errors, executed tasks, follow-up tasks, citations, and orchestration duration. The native orchestrator, planner, and role service also emit Python-compatible `agentic_*` aliases for locally available request/duration/stage/LLM/plan/scope/retrieval/task/verification/error metrics while retaining the existing `rag_agentic_*` names.
- The Agentic mock fixture now exercises a two-task plan for project purpose and backend services. This covers basic Agentic request routing, retrieval, multi-task role orchestration, stage SSE chunks, streamed answer text, and citation result shape. Remaining parity is exact Python Agentic graph/event behavior, not adopting LangGraph in .NET.
- Added SSE JSON-path fixture assertions so Agentic mock parity now verifies that final stream events expose `citations.results` as an array.
- Added `RAG-GEN-AGENTIC-SCOPE-FOLLOWUP-MOCK-001`, a deterministic Python/.NET Agentic fixture covering scope-only planning, scope-result replanning, verification-triggered follow-up retrieval, revised synthesis, final answer content, and citation payload shape.
- Hardened the OpenAI-compatible chat client with one retry for transient non-streaming `HttpRequestException` send failures. This keeps concurrent Agentic role calls resilient to premature-close behavior from local deterministic mock/NIM-compatible endpoints while recreating the HTTP request for the retry.
- Chroma now implements the detailed `IVectorStoreManagement.ListCollectionsAsync` path explicitly. It returns `VectorStoreCollectionDetails` with collection names, entity counts, and Python-style `metadata_schema` / `document_info` data when Chroma contains compatible system collections, keeping management/list behavior interface-driven across Chroma and Milvus.
- Chroma explicit filter translation now handles Python-style `source["field"]` selectors by mapping them to flattened Chroma metadata keys such as `source.source_id`. Chroma search result metadata conversion also preserves numeric and boolean metadata as stable strings instead of assuming every returned JSON value is already a string.
- VLM/image citation asset resolution now recognizes flattened nested source keys (`source.source_location`), nested Python `source` JSON, nested `content_metadata` JSON, object-valued `asset_object_names` arrays, inline data URIs, and Python/NV-Ingest thumbnail aliases (`thumbnail_id`, `thumbnail_uri`, `thumbnail_object_name`, plus `source.*` variants) in addition to existing `stored_image_uri`, `source_location`, and string-list `asset_object_names` metadata.
- Full Python upload baseline preflight was rerun with Python ingestor, Redis, object store, vector store, and deterministic mock embedding endpoint reachable. The only remaining preflight failure is NV-Ingest on `localhost:7670`; no local `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0` image or `NGC_API_KEY`/`NVIDIA_API_KEY` was available in the shell.

## Current Remaining Plan

This is the authoritative remaining-work view after the latest .NET parity slices. Older sections above are retained as session history.

### Runtime-Blocked / Fixture-Blocked

- **Agentic RAG .NET-native workflow parity**
  - Remaining: exact Python Agentic graph semantics, exact aggregate metric rollups, and live-baseline timing comparison for structured planner/task/verification events. .NET must remain a native service workflow and must not take a LangGraph dependency.
  - Current .NET state: backend Agentic is gated by `ENABLE_AGENTIC_RAG=true`; non-streaming responses work; streaming responses now flush stage SSE chunks from an orchestration event sink while planning, retrieval, synthesis, verification, and follow-up work runs. Final synthesis uses provider streaming deltas when SSE is active, with a buffered-answer fallback if a provider yields no content deltas.
  - Current metric state: .NET emits Agentic request/error/task/follow-up/citation counters and orchestration duration through `RagMetrics`, plus Python-compatible `agentic_*` aliases for request count, request duration in milliseconds, stage duration in milliseconds, LLM calls/duration/tokens including planner calls, planned tasks, scope discovery rounds, retrieval calls/chunks, task outcomes/attempts, verification outcomes, follow-up task counts, and errors. Exact aggregate rollups still need a live Python metric baseline.
  - Current fixture state: deterministic Python/.NET Agentic fixtures pass through planner, multiple task answers, synthesis, verification, retrieval, stage SSE chunks, streamed answer text, citation result shape, scope-only planning, scope-result replanning, verification-triggered follow-up retrieval, and revised synthesis.
  - Next unblocker: live Python/.NET Agentic runtime fixtures that compare exact graph transitions, token/stage timing, exact citations, and aggregate metrics beyond deterministic mock behavior.

- **NV-Ingest/NRL visual asset parity**
  - Remaining: native NV-Ingest-created image/table/chart assets, Python thumbnail IDs, and live object-store asset fixture comparisons.
  - Current .NET state: bridge-provided visual asset metadata flows into vector metadata; `ICitationAssetResolver` resolves filesystem/object-store paths, raw and object-valued `asset_object_names`, flattened and nested `source` metadata, inline data URIs, and thumbnail metadata aliases; citation metadata preserves nested `content_metadata` / page/location fields; `IVlmContextAssembler` groups resolved visual context by source/page when page metadata is available.
  - Next unblocker: NV-Ingest/NRL runtime or fixture bridge that creates real visual assets in the object store.

- **Full Python upload baseline**
  - Remaining: end-to-end Python baseline with real NV-Ingest on `localhost:7670`.
  - Current preflight state: Python ingestor, Redis, object store, vector store, and mock embedding endpoint pass; NV-Ingest fails connection refused on `localhost:7670`.
  - Blocker: NVCR/NGC access or pre-pulled `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0`.

### Local Follow-Up Candidates

- **Milvus management parity**
  - Current .NET state: `IVectorStoreManagement.ListCollectionsAsync` aggregates normal collection names, row counts, Python `metadata_schema`, and Python `document_info` catalog/collection entries for Milvus. Chroma now implements the same detailed interface method with Chroma REST collection/list/get primitives and compatible system-collection reads.
  - Latest local slice: Milvus unit coverage verifies Python/NV-Ingest delete filters using `source.source_id`, compact request emission through `/v2/vectordb/collections/compact`, and non-fatal handling when Milvus rejects compaction. Chroma unit coverage verifies detailed collection info is returned through `IVectorStoreManagement`.
  - Remaining local slice: none identified. Remaining delete/update/compaction work requires a live Python/NV-Ingest Milvus baseline with system collections so .NET behavior can be compared end-to-end.

- **Query decomposition runtime fixture parity**
  - Current .NET state: YAML prompts, single-query fast path, multi-query flow, follow-ups, merged retrieval, score normalization, Python history formatting, and final prompt rendering tests are covered.
  - Current fixture state: shared Python/.NET RAG mock matrix exists for baseline health, no-KB generation, Python-schema Milvus search, KB generation with multi-query decomposition enabled, and the single-query decomposition fast path. Generation fixtures reconstruct SSE delta text and assert deterministic answer content.
  - Remaining local slice: none identified beyond adding new fixtures if another Python behavior gap is found.

- **Provider-specific filter compatibility**
  - Current .NET state: Chroma explicit filters support nested `AND`/`OR`, ranges, `in`/`not in`, booleans/numbers, single/double-quoted metadata selectors, and Python-style `source["field"]` selectors mapped to flattened Chroma metadata. Milvus owns generated-filter support.
  - Remaining local slice: extend concrete provider parsers only when a supported Chroma/Milvus filter shape is found missing.

- **Summary status persistence**
  - Current .NET state: summary status transitions, terminal timestamps, and optional file-backed cross-process persistence are covered.
  - Remaining local slice: none identified unless Redis-native status sharing becomes a deployment requirement.

### Deferred / Out Of Scope For Current .NET Target

- Elasticsearch and LanceDB native providers remain copied-not-used or external-bridge-owned.
- VLM reranker multimodal payload support remains deferred until visual asset fixtures are available.
- Exact Python health response detail remains lower priority than endpoint availability and provider-specific health checks.

## Launchable Remote Baseline Notes - June 27, 2026

- Repo operation skill note: `skills/rag-blueprint/SKILL.md` was not present in this checkout, so remote deployment validation used local compose/docs/scripts directly.
- Launchable VM `62.169.159.90` is reachable over SSH as `shadeform` with `/tmp/rag-parity-codex-ssh`.
- Direct local access to published remote service ports timed out, but the same services were healthy from inside the VM. Local fixture execution used SSH tunnels: `18082->8082`, `17670->7670`, `16379->6379`, `19010->9010`, `19200->9200`, and `19080->9080`.
- Python ingestor/NV-Ingest preflight passed through the tunnel and wrote `/tmp/python-full-baseline-launchable-preflight.json`.
- Full Python ingestor baseline wrote `/tmp/python-full-baseline-launchable.json`. Passing fixture IDs: `ING-COL-001`, `ING-META-001`, `ING-SUMOPT-001`, `ING-DOC-001`, `ING-DOC-002`, `ING-STS-001`, and `ING-HEALTH-001`.
- Live Python baseline differences/failures: several upload fixtures returned collection-missing HTTP 500 responses instead of auto-creating collections, `ING-DEL-001` returned a message without expected `not found` text, `ING-METRICS-001` omitted `collection_info.doc_type_counts`, and `ING-BRIDGE-001` returned 404 because `/bridge/extract` is not exposed by this deployment.
- Python `rag-server` was not running. Starting `deploy/compose/docker-compose-rag-server.yaml` failed because `NGC_API_KEY` is required for `services.rag-server.environment.NVIDIA_API_KEY`, and no remote compose env file defined `NGC_API_KEY` or `NVIDIA_API_KEY`. Do not print raw keys when unblocking this.
- A durable runbook for this remote validation path now lives in `remote-parity-plan.md`.
- Follow-up after providing `NGC_API_KEY`: `rag-server` and `rag-frontend` started successfully by passing the local key to the remote compose process through stdin, without printing it or writing it to remote env files.
- Remote Python RAG dependency health passed inside the VM for Elasticsearch, object storage, LLM, embeddings, and ranking.
- Live RAG fixture output was written to `/tmp/python-rag-launchable.json`. After enabling tracing, creating `/tmp-data/prom_data`, and updating fixture expectations for live Python metrics/search shape, passing IDs are `RAG-HEALTH-001`, `RAG-METRICS-001`, `RAG-GEN-001`, `RAG-SRCH-001`, and `RAG-SUM-001`.
- Live Python RAG metrics emit `api_requests_total`. Live Python RAG search returns successful `total_results` and `results` payloads, including text/image citation metadata, without a top-level `message` wrapper.
- Validation after the Launchable RAG fixture expectation updates: unit tests passed with 207 tests, integration tests passed with 3 tests, full .NET solution build passed with 0 warnings/errors, and `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet` passed.
- Launchable ingestor classification was retried with pre-created `parity_*` collections and `--timeout 420`. `ING-AUTOCREATE-001`, `ING-OBJSTORE-001`, `ING-DUP-001`, `ING-UNSUPPORTED-001`, `ING-UNSUPPORTED-002`, `ING-DEL-001`, and `ING-METRICS-001` passed. `ING-BRIDGE-001` remains a remote image/version mismatch because the running image has no `/bridge/extract` route, unlike this checkout. The local runner now supports `--timeout` / `INGESTOR_FIXTURE_TIMEOUT_SECONDS` and defaults to 300 seconds for future live NV-Ingest runs.
- Live Agentic baseline was captured after recreating the remote RAG container with `ENABLE_AGENTIC_RAG=True`. The request wrote `/tmp/agentic-live.sse` on the VM and produced 93 parsed SSE events with `stage_start`, `stage_end`, `intermediate_reasoning`, `intermediate_output`, `final_reasoning`, and `final_answer`. Observed stages were `initial_retrieval`, `plan`, `execute`, and `synthesize`; citations were present and final `finish_reason=stop` was emitted. No `verify` stage was present because `AGENTIC_VERIFICATION_ENABLED` is false by default.
- Live visual/VLM baseline was captured from `multimodal_data`. Targeted search returned image/text/table results with base64 visual content, page/location metadata, captions, and object-store URIs such as `s3://default-bucket/multimodal_data/woods_frost.pdf/5.png`. VLM-enabled generation wrote `/tmp/vlm-live.sse`, produced 112 parsed SSE events, included citations and reasoning content, and ended with `finish_reason=stop`.
- .NET ingestor delete reporting was aligned with live Python wording: missing documents now report `do not exist in the vectorstore` instead of `not found`. Validation after P0 continuation: unit tests passed with 207 tests, integration tests passed with 3 tests, full .NET solution build passed with 0 warnings/errors, and `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet` passed.
- Final Launchable ingestor retry result with pre-created collections and `--timeout 420`: `ING-AUTOCREATE-001`, `ING-OBJSTORE-001`, `ING-DUP-001`, `ING-UNSUPPORTED-001`, `ING-UNSUPPORTED-002`, `ING-DEL-001`, and `ING-METRICS-001` passed. `ING-BRIDGE-001` still returns 404 on the remote image because that image does not register `/bridge/extract`, while this checkout contains the route. Fixture updates from this retry: auto-create upload now asserts Python-common document fields, `.rst` unsupported text accepts both live Python and .NET wording, and local filesystem object-store side effects are optional when `APP_OBJECT_STORE_ROOT` is unset for remote S3-backed validation.
- Validation after the final ingestor retry updates: unit tests passed with 207 tests, integration tests passed with 3 tests, full .NET solution build passed with 0 warnings/errors, and `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet` passed.
- Agentic verification-stage baseline was captured after recreating the remote RAG container with `AGENTIC_VERIFICATION_ENABLED=true`. The request wrote `/tmp/agentic-verify-live.sse` and `/tmp/agentic-verify-request.json` on the VM. The stream produced 124 parsed OpenAI-compatible SSE chunks with top-level `event_type` and `stage`; observed stages were `initial_retrieval`, `plan`, `execute`, `synthesize`, and `verify`. Verification emitted a pass payload and `Answer looks complete.`, citations were present, and final `finish_reason=stop` was emitted.
- Query-decomposition live baseline was captured after enabling `ENABLE_QUERY_DECOMPOSITION=true` at container env level. The remote Prompt schema does not expose `enable_query_decomposition`, so the per-request JSON field is ignored by this Launchable image. The request wrote `/tmp/query-decomposition-live.sse` and `/tmp/query-decomposition-request.json`; the stream returned normal generation chunks with citations and `finish_reason=stop`, while server logs confirmed the iterative decomposition path: 2 generated subqueries, second-query rewrite, per-subquery retrieval, no follow-up, and final response generation.
- Milvus management live parity cannot be run on the Launchable VM without disruptive reconfiguration because that deployment is Elasticsearch-backed and has no Milvus service. A fast local Python ingestor management baseline was run against local Milvus `127.0.0.1:19530` without slow uploads; `/tmp/python-milvus-management-live-parity.json` shows `ING-COL-001`, `ING-META-001`, and `ING-HEALTH-001` passing. `ING-METRICS-001` is upload/fixture-order dependent in this no-upload run because older seeded collections appear first and empty parity collections have no document-info metrics. Raw Milvus REST confirmed Python-created `metadata_schema` and `document_info` system collections with the expected fields.
- Agentic verification and query-decomposition fixture hardening followed the live baselines. `.NET` Agentic streaming now emits the raw verification JSON as `intermediate_output` on the `verify` stage before the `stage_end` event, matching the live Python verification-enabled stream. `fixtures/run_api_fixtures.py` now supports stream JSON field equality and field-substring assertions. `RAG-GEN-QD-ENV-MOCK-001` covers deployed-image behavior where query decomposition is enabled only through `ENABLE_QUERY_DECOMPOSITION=true`; the mock parity matrix runs QD fixtures in a separate env-enabled service lifecycle so Agentic fixtures remain isolated from the global QD default. `/tmp/rag-mock-parity-matrix-after-agentic.json` passed for `RAG-HEALTH-001`, `RAG-GEN-MOCK-001`, `RAG-SRCH-MOCK-001`, `RAG-GEN-QD-MOCK-001`, `RAG-GEN-QD-SINGLE-MOCK-001`, `RAG-GEN-QD-ENV-MOCK-001`, `RAG-GEN-AGENTIC-MOCK-001`, and `RAG-GEN-AGENTIC-SCOPE-FOLLOWUP-MOCK-001`.
- Launchable failure baseline was rerun under the supported Elasticsearch-backed RAG configuration without stopping shared services, using request-level unreachable endpoints. Output was written on the VM to `/tmp/python-failure-supported-baseline.json`. `FAIL-VDB-001` with `vdb_endpoint=http://127.0.0.1:9` returned HTTP `503` and `Vector database (Elasticsearch) is unavailable...`, which clears the earlier unsupported-vector-store blocker. `FAIL-RERANK-001` with `reranker_endpoint=http://127.0.0.1:9` also returned HTTP `503` and `Reranker NIM unavailable...`; .NET has now been aligned to this backend-failure behavior.
- Launchable bridge route verification confirmed the remote deployment/image mismatch. The remote checkout under `/home/shadeform/rag` has no `bridge/extract` route in `src/nvidia_rag/ingestor_server/server.py`, the running `nvcr.io/nvstaging/blueprint/ingestor-server:2.6.0` container registered no bridge routes, and direct `POST http://127.0.0.1:8082/bridge/extract` returned HTTP `404`. `ING-BRIDGE-001` remains blocked on deploying a newer image/source, not configuration.
- Launchable Milvus upload/document-info smoke was attempted without replacing the Elasticsearch-backed baseline. `milvusdb/milvus:v2.6.5` and `milvus-etcd` were started on the existing `nvidia-rag` network, then a temporary `ingestor-server-milvus` container reused the live ingestor image with `APP_VECTORSTORE_NAME=milvus` and `APP_VECTORSTORE_URL=http://milvus-standalone:19530` on port `18084`. Health passed for Milvus, object storage, embeddings, NV-Ingest, and Redis. `/tmp/python-milvus-upload-live-parity.json` captured successful `parity_milvus_upload` collection creation plus Python Milvus `metadata_schema` and `document_info` system collection creation. The first upload attempt hit the deployed image's older multipart contract (`data` field required), and the retry entered the live NV-Ingest blocking path for a one-line text document but did not complete inside the smoke-test window; `num_entities` stayed `0` and no document-info aggregation was present. The temporary ingestor, Milvus, and etcd were stopped afterward; main RAG/ingestor/Elasticsearch services were left running.
- `.NET` reranker outage behavior was aligned to live Python. `HttpRerankerClient` now wraps unreachable reranker HTTP failures with `Reranker NIM unavailable at ...`, and `RagService.ApplyRerankingAsync` records metrics/logs then rethrows instead of falling back to vector-score ordering. `FAIL-RERANK-001` now expects backend-unavailable `5xx` with a JSON `message`. Unit coverage verifies `SearchAsync` returns HTTP `502` for an unavailable reranker. Validation after this update: unit tests passed with 208 tests, integration tests passed with 3 tests, full .NET solution build passed with 0 warnings/errors, and `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet` passed.
- A temporary remote bridge sidecar was started because the main Launchable ingestor image still lacks `/bridge/extract`. The sidecar runs on `http://127.0.0.1:18085/bridge/extract` from the existing ingestor image with an explicit Python entrypoint and a mounted bridge service script. `/tmp/bridge-live-validation.json` confirms the bridge contract with non-empty text, `document_info.total_elements`, `document_info.raw_text_elements_size`, `document_info.ingestion_backend`, and an `asset_object_names` list. The main `ingestor-server` on `8082` remains unchanged and still lacks the route.
- Launchable Milvus upload/document-info was retried with text-only extraction to rule out multimodal cost. The temporary Milvus ingestor disabled image/table/chart/infographic/page-image extraction and dynamic batching, used `NV_INGEST_FILES_PER_BATCH=1`, and passed dependency health. `/tmp/python-milvus-text-upload-live-parity.json` captured successful `parity_milvus_text_upload` collection creation, but a one-line text upload timed out after 180 seconds. `/tmp/ingestor-milvus-text-upload.log` shows the job entered NV-Ingest batch mode, enabled embedding, and stalled after `Performing ingestion for batch 1`; collection listing stayed at `num_entities=0` and no document-info aggregation. The temporary Milvus ingestor, Milvus, and etcd were stopped; the bridge sidecar and main Elasticsearch-backed stack remain running.
- Final Milvus/NV-Ingest stall debugging was captured in `/tmp/launchable-parity-artifacts` and `/tmp/launchable-parity-artifacts.tgz`. The corrected non-blocking upload returned HTTP `200` with task id `435b40d0-9350-4629-8937-ff7207bc5343`; Redis kept that task `PENDING` with `debug-doc.txt` in `submitted`, and the NV-Ingest server job `2ee58d98-68f6-7200-5ff6-3b7d7345a799` stayed `SUBMITTED`. Ingestor logs show text extraction succeeded, embedding was configured, and the job was submitted to `ingest_task_queue`; NV-Ingest health remained ready and Ray cluster status showed idle resources (`0.0/28.0 CPU`, no resource demand). This narrows the live blocker to jobs accepted by NV-Ingest but not processed from the queue in this Launchable runtime, rather than Milvus control-plane, multipart shape, document size, or multimodal extraction cost.
- Post-termination parity classification on June 28, 2026: `OPS-TRACE-001` was explicitly skipped for the current pass. `ING-BRIDGE-001` is no longer tracked as a current-code implementation fail because the current checkout exposes `/bridge/extract`, the reduced local Python baseline passed, the temporary Launchable sidecar validated the contract, and .NET passes the adapter fixture; only the old main Launchable image lacked the route, so the remaining action is future deployed-image revalidation. `ING-VDBCTX-001`, `ING-STS-002`, and `OPS-INGEST-TELEMETRY-001` were initially .NET-passing extension fixtures with no captured Python equivalent before VM termination; the later local fallback run added targeted Python evidence for these API/status/telemetry behaviors. Native NV-Ingest-created visual assets and exact Milvus write-path parity remain deferred until a known-good Milvus-backed Python/NV-Ingest runtime is available, because the final debug captured an NV-Ingest queue-processing stall after successful dependency health and collection/system-collection creation.
- Implemented the targeted local harness pieces for the post-termination plan. `fixtures/run_ingestor_fixtures.py` now resolves the `REPLACE_WITH_VECTOR_DB_TOKEN_IF_REQUIRED` header from `APP_VECTORSTORE_TOKEN`, `APP_VECTORSTORE_API_KEY`, `MILVUS_TOKEN`, or runtime state, and drops the header when no token is configured so unauthenticated local runs do not send a bogus bearer token. The same runner now accepts `--log-file` / `INGESTOR_FIXTURE_LOG_FILE` and validates required `ingestion.*` checkpoint names from markdown expected files, so `OPS-INGEST-TELEMETRY-001` no longer passes on HTTP status alone. `remote-parity-plan.md` records the concrete commands for bridge revalidation, `ING-VDBCTX-001`, `ING-STS-002` runtime-id reuse after restart, and telemetry checkpoint validation.
- Local items 1-3 retry on June 28, 2026 used `deploy/compose/dotnet-local.env` as the base configuration with Milvus active, plus explicit Python overrides for local Redis, SeaweedFS, Ollama, and Milvus. Python ingestor on `127.0.0.1:18082` reported Milvus/object-store/Redis healthy. `ING-BRIDGE-001` passed and wrote `/tmp/bridge-local-validation.json`. Milvus management passed `ING-COL-001`, `ING-META-001`, and `ING-HEALTH-001`; `ING-METRICS-001` still lacked document-info fields because no upload completed. `ING-DOC-002` passed and stored task id `6d4038ae-2016-4e0e-b745-53761639a285`; Redis shows that task `PENDING`, so `ING-STS-002` failed on expected `FINISHED`. `ING-VDBCTX-001` and `OPS-INGEST-TELEMETRY-001` timed out in blocking upload after entering the NV-Ingest path. A local NV-Ingest compose retry pulled `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0`, but the container exited `139` with `qemu: uncaught target signal 11 (Segmentation fault)` on this arm64 host and also logged unresolved default OCR hostnames (`nemotron-ocr`). `deploy/compose/docker-compose-ingestor-server.yaml` now makes the NV-Ingest runtime `MESSAGE_CLIENT_HOST`/`MESSAGE_CLIENT_PORT` honor `REDIS_HOST`/`REDIS_PORT`, enabling reuse of the already-running dockerized Redis via `host.docker.internal` on future local retries.
- Local fallback workaround on June 28, 2026 addresses the local architecture blocker without changing production defaults. `APP_NVINGEST_LOCAL_FALLBACK=true` routes Python ingestion through a narrow text-only fallback that extracts document text, truncates oversized Milvus varchar fields by UTF-8 byte length, writes minimal Python-compatible Milvus rows, writes collection/document `document_info`, updates task status to `FINISHED`, and emits `ingestion_checkpoint` log events. This is only for parity harness validation on hosts where the NV-Ingest container exits under amd64 emulation; it does not claim native NV-Ingest multimodal asset, thumbnail, Ray queue, or exact extraction parity. With the fallback enabled, `ING-DOC-002` passed in `/tmp/python-fallback-doc002-local.json`, and `ING-STS-002`, `OPS-INGEST-TELEMETRY-001`, and `ING-METRICS-001` passed in `/tmp/python-fallback-status-telemetry-metrics.json`; earlier local fallback/control-plane evidence covers `ING-VDBCTX-001`.
- .NET API/UI smoke on June 28, 2026 used `deploy/compose/dotnet-local.env` with local Milvus, Redis, Chroma still available, and Ollama models. Unit tests passed with 208 tests, integration tests passed with 3 tests, full solution build passed with zero warnings/errors, and compose config validation passed. Runtime smoke started reranker, RAG API, ingestor API, and Blazor UI on temporary local ports. Reranker `/health`, RAG `/v1/health?check_dependencies=true`, RAG `/metrics`, ingestor `/v1/health?check_dependencies=true`, ingestor `/collections`, Blazor `/`, `/collections/new`, `/settings`, and referenced static assets all returned HTTP 200. Blazor startup now accepts both `RagServer__BaseUrl` / `IngestorServer__BaseUrl` and shorter `RagApi__BaseUrl` / `IngestorApi__BaseUrl` aliases; corrected UI logs confirmed calls went to `http://127.0.0.1:18081` and `http://127.0.0.1:18085` with HTTP 200 responses. Blazor chat is routed at `/`; `/chat` is not a route.
- Local .NET sanity launch on June 28, 2026 used `deploy/compose/dotnet-local.env`, local Milvus REST on `19530`, Ollama `nomic-embed-text` with `APP_EMBEDDINGS_DIM=768`, and `data/multimodal/product_catalog.pdf`. Repo operation skill note: `skills/rag-blueprint/SKILL.md` was not present in this checkout, so local launch validation used compose/env/docs directly. Preflight passed: unit tests `208/208`, integration tests `3/3`, solution build with zero warnings/errors, and `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`. API service launches required unsandboxed local execution to bind ports; sandboxed launches stayed alive without listening. Reranker bound on `8083`, RAG on `8081`, ingestor on `8082`, and Blazor used its launch profile port `5154`.
- Local sanity results: PASS `GET /health` on reranker returned HTTP 200; PASS RAG `GET /v1/health?check_dependencies=true` returned HTTP 200 with Milvus and Ollama configured; PASS ingestor `GET /v1/health?check_dependencies=true` returned HTTP 200 with Milvus, filesystem object storage, local backend, and file task persistence healthy; PASS RAG `/metrics` returned Prometheus text. PASS reset/create for `sanity_check`: delete reported collection absent, then `POST /collection` created it. PASS non-blocking PDF upload returned task `03dd7d1a-6e5f-479e-b7a0-ecf20b293e09`, which reached `FINISHED` with `documents_completed=1`, `total_elements=258`, `raw_text_elements_size=20661`, and generated document id `2e133d27-6bae-4eec-819c-84159fe589f0`. PASS document listing showed `product_catalog.pdf`; PASS collection listing showed `sanity_check` with `num_entities=1`, `number_of_files=1`, `doc_type_counts.pdf=1`, and ingestion status `completed`.
- Local RAG/reranker sanity results: PASS `/v1/search` against `sanity_check` with `enable_reranker=true` returned HTTP 200 and 5 ordered catalog chunks; logs confirmed the RAG server called reranker-service and reranked 8 chunks to 5. PASS `/v1/search` with `enable_reranker=false` returned HTTP 200 and vector-score ordering. PASS direct `POST /v1/rerank` with the correct DTO shape (`id`, `text`, `score`, `metadata`) returned HTTP 200, `provider_used=ollama`, and ranked the product-related synthetic chunk first. WARN/FAIL generation under the unmodified local env: first `/v1/generate` returned HTTP 200 SSE but emitted `Failed to parse JSON` after Agentic planner attempts because `ENABLE_AGENTIC_RAG=True` and local `nemotron-mini` did not produce planner JSON. A second request with `agentic=false` still used env-enabled query rewriting/decomposition and env-only reflection; it rewrote the catalog question to an unrelated memory-improvement query, entered reflection regeneration, and was cancelled after roughly 144 seconds. Fix plan: make local sanity generation profiles disable Agentic/query rewriting/query decomposition/reflection at startup, or change the server merge semantics so explicit request `false` can override env defaults for generate; also consider adding an `enable_reflection` request flag if per-request disabling is intended.
- Local Blazor sanity results: PASS `GET /` on Blazor returned HTTP 200 and prerendered the collection sidebar with `sanity_check` showing `1 entities`, confirming Blazor-to-ingestor configuration. WARN interactive UI chat was not browser-driven because Playwright is not installed in this checkout, and default-env generation was already failing/slow at the API layer. Fix plan: add a repeatable UI smoke harness or document the dependency for driving Blazor Server interactions, then rerun chat after the generation profile issue is isolated.
- Follow-up Agentic true/false sanity on June 28, 2026: confirmed `GET /v1/configuration` reported `enable_agentic_rag=true` under the original `dotnet-local.env` RAG process. A compact no-KB Agentic generation request (`use_knowledge_base=false`, `agentic=true`, `max_tokens=64`) returned HTTP 200 SSE but failed at the planner with `Failed to parse JSON` after three local Ollama planner attempts. The RAG process was then restarted with the same env plus `ENABLE_AGENTIC_RAG=False`; `GET /v1/configuration` reported `enable_agentic_rag=false`, health returned HTTP 200, and reranked search against `sanity_check` still returned HTTP 200. With Agentic disabled, a no-KB generation request streamed a normal short answer and empty citations. KB generation still hit env-enabled query rewriting/decomposition despite request fields set false, rewrote `Name two products described in the catalog.` to `What are some good ways to improve my memory?`, and continued through slow decomposition/reflection work until the client was cancelled. Classification: PASS Agentic false disables the Agentic planner for no-KB generation; FAIL Agentic true local planner with `nemotron-mini` is not producing required JSON; WARN/FAIL KB generation remains affected by separate env-default query rewriting/decomposition/reflection behavior even when Agentic is disabled.
- Local ChromaDB .NET/UI sanity on June 28, 2026 used `deploy/compose/dotnet-local.env` with `APP_VECTORSTORE_NAME=chroma`, `APP_VECTORSTORE_URL=http://localhost:8000`, host Ollama, and `data/multimodal/product_catalog.pdf`. Repo operation skill note: `skills/rag-blueprint/SKILL.md` was not present in this checkout, so validation used compose/env/docs directly. Chroma heartbeat passed despite Docker marking the container unhealthy. Reranker, RAG, ingestor, and Blazor were launched on `8083`, `8081`, `8082`, and `5154`; local sandboxed launches stayed alive without listening, so unsandboxed local execution was required to bind the ports. Chroma collection listing was fixed so `IngestorService.GetCollections` merges vector-store management details into the durable catalog response, and Chroma count retrieval now uses the native `/collections/{id}/count` endpoint with a `/get` fallback. This changed `sanity_check_ui` from catalog document count to vector entity count after upload; live Chroma returned 8 chunks and `/collections` reported `num_entities=8`.
- Final Chroma sanity harness output was written to `/tmp/blazor-chroma-sanity-report-final.json` with screenshots in `/tmp/blazor-chroma-sanity-screens-final`. Summary: 47 PASS, 2 WARN, 2 FAIL. PASS coverage included RAG/ingestor/reranker/Blazor health, PDF upload to Chroma, completed ingestion with nonzero elements, Chroma entities, product-catalog search, direct reranker dependency, RAG search with reranker, prompt catalog keys, Agentic prompt file loading, runtime configuration, Standard no-KB and KB generation, history handling, product questions, sidebar search, collection selection chip, details drawer, product-aware UI answer with citations, deselection, Agentic option visibility, Stop, and Clear chat. WARN items were unsupported `tool` and empty roles being accepted or ignored without a clear API error. FAIL items were limited to local Agentic planner behavior: the stream emitted `Failed to parse JSON` and lacked product evidence. This is the same local Ollama planner-format issue already classified as report-only for this pass, not a Chroma or interface abstraction failure. Validation after the implementation: `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore --filter ChromaDbVectorStoreTests` passed 11/11, `uv run python -m py_compile fixtures/run_blazor_sanity.py` passed, and `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore` passed with zero warnings/errors.
- Follow-up Chroma auxiliary-collection cleanup on June 28, 2026 fixed the summary companion leak. `.NET` summarization writes vector summaries into `summary_{collectionName}`; deleting the base collection now deletes owned auxiliary vector collections through `IVectorStoreManagement`, removes stale auxiliary catalog entries, deletes matching object-store artifacts, and keeps `summary_*` collections out of the user-facing `/collections` response so Blazor does not show internal summary collections. Live Chroma proof used `delete_probe` plus direct Chroma-created `summary_delete_probe`: `/collections` reported `has_base=true` and `has_summary=false`, `DELETE /collections ["delete_probe"]` returned success, direct Chroma listing returned `[]` for both names, and ingestor `/collections` also returned `[]` for both names. Final validation: focused Chroma/delete tests passed 13/13, full unit suite passed 210/210, full solution build passed with zero warnings/errors, and RAG `8081`, ingestor `8082`, reranker `8083`, and Blazor `5154` health checks passed against local Chroma/Ollama.
- Blazor settings persistence regression fixed on June 29, 2026. `/settings` was reapplying `/v1/configuration` defaults on every page initialization, so user-edited values such as `Temperature=0.3` and `EnableQueryRewriting=true` reverted to server defaults after navigating away and back. `SettingsState.ApplyServerDefaults` now applies backend defaults once per circuit, JSON restore marks defaults as applied, and RAG/feature setting controls notify state changes so local-storage persistence can save edited values when enabled. Playwright validation against live Blazor `5154` changed Temperature to `0.3`, enabled Query Rewriting, navigated to Chat and back, created `settings_probe_ui`, returned to Settings, and observed `temperature=0.3` plus `query_rewriting=true` both before and after collection creation. Probe cleanup deleted `settings_probe_ui`; full solution build passed with zero warnings/errors and unit tests passed 210/210.
