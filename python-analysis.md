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
- Live `.NET` RAG fixture run against local Milvus/Ollama passed `RAG-HEALTH-001`, `RAG-METRICS-001`, and `RAG-SRCH-001`; server logs confirmed search called Ollama at `POST http://localhost:11434/api/embed`.

## .NET RAG Failure and Trace Fixture Automation

- Added `fixtures/run_dotnet_rag_fixtures.py` to start an isolated `.NET` `rag_server` process with scenario-specific env overrides, run selected API fixtures, and shut the process down cleanly.
- The runner has deterministic scenarios for:
  - `normal`: local Milvus/Ollama baseline
  - `bad-reranker`: unreachable reranker service to validate fallback API behavior
  - `bad-vdb`: unreachable vector-store endpoint to validate backend failure mapping
  - `tracing`: local OTLP/HTTP capture server plus `APP_TRACING_ENABLED=true`
- Isolated `.NET` RAG runs passed:
  - `FAIL-VDB-001` with `APP_VECTORSTORE_URL=http://127.0.0.1:9`
  - `FAIL-RERANK-001` with `APP_RERANKER_SERVICE_URL=http://127.0.0.1:9`
  - full RAG API set: `RAG-HEALTH-001`, `RAG-METRICS-001`, `RAG-SRCH-001`, `RAG-GEN-001`, `RAG-SUM-001`
  - `OPS-TRACE-001` with two captured OTLP/HTTP export requests containing `.NET` RAG service/span identifiers
- Reranker failure API coverage is still data-dependent for proving the internal fallback branch, because the service only calls the reranker when vector search returns more than one candidate. The current fixture validates that the public API remains successful with an unreachable reranker URL.

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
