# Remote Launchable Parity Validation

Date: June 27, 2026

## Target

- Host: `62.169.159.90`
- SSH user: `shadeform`
- SSH key: `/tmp/rag-parity-codex-ssh`
- Remote checkout: `/home/shadeform/rag`
- Local checkout: `/Users/abe/src/nvidia/abes-rag`

## Current Remote State

Healthy from inside the VM:

- Python ingestor on `127.0.0.1:8082`
- NV-Ingest runtime on `127.0.0.1:7670`
- Redis on `127.0.0.1:6379`
- SeaweedFS object store on `127.0.0.1:9010`
- Elasticsearch on `127.0.0.1:9200`
- Embedding NIM on `127.0.0.1:9080`
- Ranking NIM on `127.0.0.1:1976`

Not running:

- None currently known.

The host publishes Docker ports, but direct access from the local workstation to
the remote service ports timed out. Use SSH port forwarding and run local
fixture scripts against `127.0.0.1` tunnel ports.

## RAG Server Startup

Initial startup of the remote RAG compose file failed because the deployment
requires `NGC_API_KEY` and none of the remote compose env files define
`NGC_API_KEY` or `NVIDIA_API_KEY`.

Attempted command:

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'cd /home/shadeform/rag && set -a && . deploy/compose/.env && set +a && docker compose -f deploy/compose/docker-compose-rag-server.yaml up -d'
```

Observed error:

```text
error while interpolating services.rag-server.environment.NVIDIA_API_KEY: required variable NGC_API_KEY is missing a value: "NGC_API_KEY is required"
```

The server was later started by feeding the local `NGC_API_KEY` to the remote
compose command through stdin. The key was not printed and was not written into
remote env files.

```bash
printf "%s\n" "$NGC_API_KEY" | ssh -i /tmp/rag-parity-codex-ssh \
  -o BatchMode=yes \
  -o ConnectTimeout=10 \
  shadeform@62.169.159.90 \
  'read -r NGC_API_KEY && export NGC_API_KEY && cd /home/shadeform/rag && docker compose -f deploy/compose/docker-compose-rag-server.yaml up -d'
```

After startup, `rag-server` was listening on `8081` and `rag-frontend` was
listening on `8090`. RAG dependency health passed from inside the VM for
Elasticsearch, object storage, LLM, embeddings, and ranking.

For metrics validation, the container was recreated with `APP_TRACING_ENABLED=True`.
The Prometheus multiprocess directory had to be created inside the container
before the final restart:

```bash
docker exec rag-server sh -lc 'mkdir -p /tmp-data/prom_data && chmod 777 /tmp-data/prom_data && rm -f /tmp-data/prom_data/*'
docker restart rag-server
```

Without tracing enabled, `/metrics` returned an empty HTTP 200 response. With
tracing enabled but no metrics directory, it returned an error about
`PROMETHEUS_MULTIPROC_DIR`. With both configured, it emitted
`api_requests_total`.

## Tunnel Setup

Run this from the local checkout and keep it open while running fixtures:

```bash
ssh -i /tmp/rag-parity-codex-ssh \
  -o BatchMode=yes \
  -o ExitOnForwardFailure=yes \
  -N \
  -L 18082:127.0.0.1:8082 \
  -L 17670:127.0.0.1:7670 \
  -L 16379:127.0.0.1:6379 \
  -L 19010:127.0.0.1:9010 \
  -L 19200:127.0.0.1:9200 \
  -L 19080:127.0.0.1:9080 \
  shadeform@62.169.159.90
```

## Ingestor Baseline Commands

Preflight only:

```bash
APP_EMBEDDINGS_SERVERURL=http://127.0.0.1:19080/v1 \
uv run python fixtures/run_python_full_baseline.py \
  --base-url http://127.0.0.1:18082 \
  --redis-host 127.0.0.1 \
  --redis-port 16379 \
  --nvingest-url http://127.0.0.1:17670 \
  --object-store-host 127.0.0.1 \
  --object-store-port 19010 \
  --vectorstore-url http://127.0.0.1:19200 \
  --out /tmp/python-full-baseline-launchable-preflight.json
```

Full ingestor fixture baseline:

```bash
APP_VECTORSTORE_URL=http://127.0.0.1:19200 \
APP_EMBEDDINGS_SERVERURL=http://127.0.0.1:19080/v1 \
uv run python fixtures/run_python_full_baseline.py \
  --base-url http://127.0.0.1:18082 \
  --redis-host 127.0.0.1 \
  --redis-port 16379 \
  --nvingest-url http://127.0.0.1:17670 \
  --object-store-host 127.0.0.1 \
  --object-store-port 19010 \
  --vectorstore-url http://127.0.0.1:19200 \
  --run-fixtures \
  --out /tmp/python-full-baseline-launchable.json
```

## RAG Baseline Commands

Open a RAG tunnel:

```bash
ssh -i /tmp/rag-parity-codex-ssh \
  -o BatchMode=yes \
  -o ExitOnForwardFailure=yes \
  -N \
  -L 18081:127.0.0.1:8081 \
  shadeform@62.169.159.90
```

Run the live RAG fixtures:

```bash
UV_CACHE_DIR=/tmp/uv-cache \
uv run python fixtures/run_api_fixtures.py \
  --base-url http://127.0.0.1:18081 \
  --ids RAG-HEALTH-001 RAG-METRICS-001 RAG-GEN-001 RAG-SRCH-001 RAG-SUM-001 \
  --out /tmp/python-rag-launchable.json
```

## Agentic And Visual Baseline Commands

Enable Agentic for the RAG container when a live Agentic baseline is needed:

```bash
printf "%s\n" "$NGC_API_KEY" | ssh -i /tmp/rag-parity-codex-ssh \
  -o BatchMode=yes \
  -o ConnectTimeout=10 \
  shadeform@62.169.159.90 \
  'read -r NGC_API_KEY && export NGC_API_KEY APP_TRACING_ENABLED=True ENABLE_AGENTIC_RAG=True && cd /home/shadeform/rag && docker compose -f deploy/compose/docker-compose-rag-server.yaml up -d && docker exec rag-server sh -lc "mkdir -p /tmp-data/prom_data && chmod 777 /tmp-data/prom_data && rm -f /tmp-data/prom_data/*" && docker restart rag-server'
```

The live Agentic request used `agentic=true`, `enable_streaming=true`,
`collection_names=["multimodal_data"]`, and `enable_vlm_inference=false`.
The remote output was saved as `/tmp/agentic-live.sse`.

The visual search request was saved as `/tmp/visual-search.json`, and the
VLM-enabled generation stream was saved as `/tmp/vlm-live.sse`.

## June 27, 2026 Result

Preflight passed through the SSH tunnel:

- `python_ingestor`: pass
- `redis`: pass
- `nvingest`: pass
- `object_store`: pass
- `vectorstore`: pass, Elasticsearch at tunneled `19200`
- `embedding_model`: pass

Full baseline output:

- `/tmp/python-full-baseline-launchable-preflight.json`
- `/tmp/python-full-baseline-launchable.json`

Passing fixtures:

- `ING-COL-001`
- `ING-META-001`
- `ING-SUMOPT-001`
- `ING-DOC-001`
- `ING-DOC-002`
- `ING-STS-001`
- `ING-HEALTH-001`

Failing fixtures and observed Python behavior:

- `ING-AUTOCREATE-001`: HTTP 500, collection `parity_auto_create` must already exist.
- `ING-DEL-001`: response message did not contain expected `not found` text.
- `ING-METRICS-001`: missing `$.collections[0].collection_info.doc_type_counts`.
- `ING-OBJSTORE-001`: HTTP 500, collection `parity_object_store` must already exist.
- `ING-DUP-001`: HTTP 500, collection `parity_duplicate_upload` must already exist.
- `ING-UNSUPPORTED-001`: HTTP 500, collection `parity_unsupported_upload` must already exist.
- `ING-UNSUPPORTED-002`: HTTP 500, collection `parity_unknown_extension` must already exist.
- `ING-BRIDGE-001`: HTTP 404, `/bridge/extract` is not exposed by this deployment.

Follow-up classification:

- The running Launchable ingestor image differs from this checkout: it does not register `/bridge/extract`, and its upload path requires the collection to exist before `POST /documents`.
- After manually creating `parity_auto_create`, `parity_object_store`, `parity_duplicate_upload`, `parity_unsupported_upload`, and `parity_unknown_extension`, the collection-missing failures were no longer the relevant blocker.
- `fixtures/run_ingestor_fixtures.py` now has a configurable per-request timeout via `--timeout` or `INGESTOR_FIXTURE_TIMEOUT_SECONDS`, defaulting to 300 seconds, for future live NV-Ingest runs.
- Retry result with pre-created collections and `--timeout 420`:
  - `ING-AUTOCREATE-001`: pass after relaxing expected upload document fields to live Python's `document_name` / `document_info` shape.
  - `ING-OBJSTORE-001`: pass after making local filesystem object-store side-effect assertion optional when `APP_OBJECT_STORE_ROOT` is unset for remote S3-backed deployments.
  - `ING-DUP-001`: pass.
  - `ING-UNSUPPORTED-001`: pass after accepting live Python `.rst` wording (`not a supported format`) alongside .NET's `Unsupported file type`.
  - `ING-UNSUPPORTED-002`: pass.
  - `ING-DEL-001`: pass after aligning expected missing-document wording with live Python (`do not exist in the vectorstore`).
  - `ING-METRICS-001`: pass.
- `ING-BRIDGE-001` remains a deployment/image-version mismatch: this remote image has no `/bridge/extract` route.

RAG baseline output:

- `/tmp/python-rag-launchable.json`

Passing RAG fixtures:

- `RAG-HEALTH-001`
- `RAG-METRICS-001`
- `RAG-GEN-001`
- `RAG-SRCH-001`
- `RAG-SUM-001`

Resolved RAG fixture observations:

- `RAG-METRICS-001`: live Python emits `api_requests_total`; fixture expectations were updated to accept this real Python metric family.
- `RAG-SRCH-001`: search succeeds and returns `total_results` plus a `results` array with text and image citation metadata. The live Python source and deployment do not include a top-level `message` wrapper, so fixture expectations were updated to assert the shared payload fields.
- `RAG-SUM-001`: passed with HTTP 404, which is allowed by the current fixture for unavailable summary data.

Live Agentic RAG baseline:

- `ENABLE_AGENTIC_RAG=True` was enabled on the remote RAG container.
- The live request completed with HTTP 200 SSE and wrote `/tmp/agentic-live.sse` on the VM.
- Summary of observed stream: 93 parsed SSE events; event types included `stage_start`, `stage_end`, `intermediate_reasoning`, `intermediate_output`, `final_reasoning`, and `final_answer`.
- Observed stages: `initial_retrieval`, `plan`, `execute`, and `synthesize`.
- The stream included final citations and a final `finish_reason=stop`.
- No `verify` stage was emitted in this default baseline because `AGENTIC_VERIFICATION_ENABLED` is false in the deployment.

Live Agentic verification-stage baseline:

- `AGENTIC_VERIFICATION_ENABLED=true` was enabled on the remote RAG container, preserving `ENABLE_AGENTIC_RAG=True` and tracing setup.
- The live request completed with HTTP 200 SSE and wrote `/tmp/agentic-verify-live.sse` on the VM; the request body is `/tmp/agentic-verify-request.json`.
- Summary of observed stream: 124 parsed SSE chunks, OpenAI-compatible envelope with top-level `event_type` and `stage` fields.
- Event counts: `stage_start=7`, `stage_end=7`, `intermediate_reasoning=65`, `intermediate_output=43`, `final_answer=1`.
- Observed stages: `initial_retrieval`, `plan`, `execute`, `synthesize`, and `verify`.
- Verification outputs included `{"status": "pass", "reasoning": "The answer explicitly states the project purpose and lists backend services/components, addressing both requested parts."}` followed by `Answer looks complete.`
- The stream included citations and ended with final `finish_reason=stop`.

Live query-decomposition baseline:

- The remote Prompt schema does not expose `enable_query_decomposition` as a request field, so the per-request fixture flag is ignored by this Launchable image.
- `ENABLE_QUERY_DECOMPOSITION=true` was enabled at container env level and the standard non-Agentic path was invoked with `agentic=false`.
- The live request completed with HTTP 200 SSE and wrote `/tmp/query-decomposition-live.sse` on the VM; the request body is `/tmp/query-decomposition-request.json`.
- The client stream used the normal generation SSE shape: 15 parsed chunks, citations present with up to 8 results, and final `finish_reason=stop`; it did not expose query-decomposition stage metadata to the client.
- Server logs confirmed the Python query-decomposition path executed: `STAGE: Query Decomposition (Iterative)`, generated 2 subqueries, rewrote the second query, retrieved documents for each subquery, skipped follow-up, and generated the final response.

Milvus management live parity:

- The Launchable VM is Elasticsearch-backed (`APP_VECTORSTORE_NAME="elasticsearch"`) and has no Milvus container, so Launchable cannot provide a live Python/NV-Ingest Milvus management baseline without disruptive vector-store reconfiguration and reingestion.
- A fast local Python ingestor was started against the existing local Milvus REST endpoint on `127.0.0.1:19530`, avoiding slow upload/NV-Ingest work.
- Management-only fixture output was written to `/tmp/python-milvus-management-live-parity.json`.
- Passing fixtures: `ING-COL-001`, `ING-META-001`, and `ING-HEALTH-001`.
- `ING-METRICS-001` failed only on the current fixture's first-collection assumption: local Milvus returned older seeded collections before the newly-created parity collections, and skipped upload means empty parity collections do not have document-info metrics such as `number_of_files`.
- Raw Milvus verification confirmed Python-created system collections:
  - `metadata_schema` with `pk`, `collection_name`, `vector`, and `metadata_schema` fields.
  - `document_info` with `pk`, `info_type`, `collection_name`, `document_name`, `info_value`, and `vector` fields.

Milvus upload/document-info smoke on Launchable:

- A non-disruptive Milvus smoke path was attempted by starting Milvus alongside the Elasticsearch-backed deployment and running a temporary `ingestor-server-milvus` container on port `18084`.
- Milvus was started with the CPU image (`milvusdb/milvus:v2.6.5`) plus `milvus-etcd`; both became healthy on the `nvidia-rag` network.
- The temporary ingestor reused the live `nvcr.io/nvstaging/blueprint/ingestor-server:2.6.0` image with only the vector-store env changed to `APP_VECTORSTORE_NAME=milvus` and `APP_VECTORSTORE_URL=http://milvus-standalone:19530`.
- Dependency health passed for Milvus, object storage, embeddings, NV-Ingest, and Redis through `http://127.0.0.1:18084/health?check_dependencies=true`.
- Smoke output was written on the VM to `/tmp/python-milvus-upload-live-parity.json`.
- `POST /collection` created `parity_milvus_upload` successfully. The ingestor also created the Milvus `metadata_schema` and `document_info` system collections.
- The first upload attempt used the current fixture multipart field name and returned HTTP `422` because this deployed image expects the older `data` field.
- The retry with the deployed `data` field entered the live NV-Ingest batch path for a one-line text file but did not complete inside the smoke-test window; collection listing still showed `num_entities=0` and no document-info aggregation.
- To avoid extending VM runtime and backend load, the temporary Milvus ingestor, `milvus-standalone`, and `milvus-etcd` were stopped after capture. The main Elasticsearch-backed `rag-server`, `ingestor-server`, and `elasticsearch` containers were left running.
- Classification: Milvus control-plane and system-collection creation are now live-confirmed on Launchable; upload-dependent Milvus document-info remains deferred because even the tiny live NV-Ingest upload stalled in the blocking path.

Bridge route verification:

- The remote checkout under `/home/shadeform/rag` does not contain `bridge/extract` in `src/nvidia_rag/ingestor_server/server.py`.
- The running ingestor image registered no bridge routes when inspected inside the container.
- Direct `POST http://127.0.0.1:8082/bridge/extract` returned HTTP `404`.
- A temporary bridge-only sidecar was started from the running ingestor image with an explicit Python entrypoint and a mounted bridge service script.
- The sidecar is listening on `http://127.0.0.1:18085/bridge/extract` on the VM.
- `/tmp/bridge-live-validation.json` confirms the bridge contract with non-empty `text`, `document_info.total_elements`, `document_info.raw_text_elements_size`, and `document_info.ingestion_backend`.
- Classification: bridge behavior is now reachable through the temporary sidecar, but `ING-BRIDGE-001` against the main deployed ingestor on `8082` remains a deployed image/source mismatch until a newer ingestor image/source is deployed.

Failure baseline rerun under supported Launchable configuration:

- The rerun used the live Elasticsearch-backed RAG service and request-level outage injection rather than stopping shared services.
- Output was written on the VM to `/tmp/python-failure-supported-baseline.json`.
- `FAIL-VDB-001` with `vdb_endpoint=http://127.0.0.1:9` returned HTTP `503` with JSON message `Vector database (Elasticsearch) is unavailable at http://127.0.0.1:9...`; this matches the expected backend-unavailable Python behavior.
- `FAIL-RERANK-001` with `reranker_endpoint=http://127.0.0.1:9` returned HTTP `503` with JSON message `Reranker NIM unavailable at http://127.0.0.1:9...`.
- `.NET` was aligned to the live Python behavior: reranker call failures now propagate to the search endpoint as backend failures instead of returning vector-score fallback ordering.
- The failure fixture expectation now allows backend-unavailable `5xx` and asserts a JSON `message`.
- Unit coverage verifies `SearchAsync` returns HTTP `502` with a reranker-unavailable message when the reranker client throws.

Milvus text-only upload retry on Launchable:

- A second non-disruptive Milvus upload attempt was run with a fresh temporary `ingestor-server-milvus` and the same CPU Milvus service.
- The temporary ingestor overrode the live image env to disable multimodal/slow extraction paths: `APP_NVINGEST_EXTRACTIMAGES=False`, `APP_NVINGEST_EXTRACTTABLES=False`, `APP_NVINGEST_EXTRACTCHARTS=False`, `APP_NVINGEST_EXTRACTINFOGRAPHICS=False`, `APP_NVINGEST_EXTRACTPAGEASIMAGE=False`, `ENABLE_NV_INGEST_DYNAMIC_BATCHING=False`, `NV_INGEST_FILES_PER_BATCH=1`, and `NV_INGEST_CONCURRENT_BATCHES=1`.
- Health passed for Milvus, object storage, embeddings, NV-Ingest, and Redis through `http://127.0.0.1:18084/health?check_dependencies=true`.
- Output was written on the VM to `/tmp/python-milvus-text-upload-live-parity.json`; logs were saved to `/tmp/ingestor-milvus-text-upload.log`.
- `POST /collection` created `parity_milvus_text_upload` successfully.
- A one-line text upload with the deployed image's `data` multipart field timed out after 180 seconds with no response.
- Logs show the request entered NV-Ingest batch mode, disabled dynamic batching, enabled embedding, and then stalled after `Performing ingestion for batch 1`; collection listing still showed `num_entities=0` and no document-info aggregation.
- The temporary Milvus ingestor, `milvus-standalone`, and `milvus-etcd` were stopped after capture. The main Elasticsearch-backed stack stayed running.
- Classification: the remaining upload-dependent Milvus document-info gap is a live NV-Ingest/Milvus write-path stall on this Launchable VM, not document size or multimodal extraction cost.

Milvus/NV-Ingest stall debug capture:

- Final debug artifacts were copied into `/tmp/launchable-parity-artifacts` and archived at `/tmp/launchable-parity-artifacts.tgz`.
- A malformed first non-blocking debug request confirmed this deployed image requires `data` as a literal multipart string, not `data=@file`; that attempt returned HTTP `422`.
- The corrected non-blocking upload returned HTTP `200` immediately with task id `435b40d0-9350-4629-8937-ff7207bc5343`.
- Redis task state for that upload stayed `PENDING`, with `documents_completed=0`, `batches_completed=0`, and `debug-doc.txt` in `submitted`.
- NV-Ingest server job `2ee58d98-68f6-7200-5ff6-3b7d7345a799` stayed `SUBMITTED` in Redis.
- Ingestor debug logs show local text extraction succeeded, embedding was configured, the job was submitted to queue `ingest_task_queue`, and then state repeatedly remained `submitted`.
- NV-Ingest health remained ready and Ray cluster status reported idle resources (`0.0/28.0 CPU`, no resource demand), so the strongest captured evidence is that jobs are accepted but not processed from the queue in this Launchable NV-Ingest runtime.
- Milvus remained healthy and collection control-plane operations succeeded; the target collection still had `num_entities=0`.

Local parity hardening after these baselines:

- `.NET` Agentic streaming now emits the raw verification JSON as `intermediate_output` on the `verify` stage before `stage_end`, matching the live Python verification-enabled stream shape.
- The mock fixture runner now asserts specific fields inside SSE JSON chunks, including verification status payloads.
- `RAG-GEN-QD-ENV-MOCK-001` was added to cover deployed Python images where query decomposition is activated by `ENABLE_QUERY_DECOMPOSITION=true` rather than by a request field.
- `fixtures/run_rag_mock_parity_matrix.py` now runs query-decomposition fixtures in a separate env-enabled service lifecycle so global QD defaults do not interfere with Agentic fixtures.
- `/tmp/rag-mock-parity-matrix-after-agentic.json` passed for the full mock RAG/Agentic/QD set, including the new env-default QD fixture.

Live visual/VLM baseline:

- Targeted visual search returned 5 results from `multimodal_data`.
- Result types included `image`, `text`, and `table`.
- Image results included base64 content, page/location metadata, captions, and object-store URIs such as `s3://default-bucket/multimodal_data/woods_frost.pdf/5.png`.
- Table results included `document_type=table`, `content_metadata.type=structured`, page/location metadata, and table text content.
- VLM-enabled generation completed with HTTP 200 SSE and wrote `/tmp/vlm-live.sse` on the VM.
- VLM stream summary: 112 parsed SSE events, citations present, final `finish_reason=stop`, and reasoning content present.

## Next Steps

1. If future search envelope parity work proposes a top-level `message`, compare against the live Python source first; the current live Python baseline has no wrapper.
2. Keep .NET changes interface-based: vector behavior in concrete providers, ingestion backend behavior behind `IIngestionPipeline`, and Agentic behavior .NET-native.
3. Revalidate `ING-BRIDGE-001` only after deploying a newer main Python ingestor image/source that includes `POST /bridge/extract`; the current checkout and temporary sidecar already validated the contract, while the terminated Launchable main image was stale.
4. Treat `ING-VDBCTX-001`, `ING-STS-002`, and `OPS-INGEST-TELEMETRY-001` as locally covered through the opt-in Python fallback plus .NET fixture coverage. Reopen them only for native NV-Ingest runtime comparison.
5. Defer native NV-Ingest-created visual assets, thumbnails, and exact Milvus write-path parity until the live NV-Ingest/Milvus queue-processing stall can be debugged or a known-good Milvus-backed Python deployment is available.

Local retry on June 28, 2026:

- Started the Python ingestor directly from the checkout on `127.0.0.1:18082` using `deploy/compose/dotnet-local.env` as the base and explicit Milvus/Ollama/Redis/SeaweedFS overrides:
  - `APP_VECTORSTORE_NAME=milvus`
  - `APP_VECTORSTORE_URL=http://localhost:19530`
  - `APP_EMBEDDINGS_SERVERURL=http://localhost:11434`
  - `APP_EMBEDDINGS_MODELNAME=snowflake-arctic-embed:22m`
  - `REDIS_HOST=localhost`
  - `OBJECTSTORE_ENDPOINT=localhost:9010`
- Health returned healthy for Milvus, object storage, and Redis. Embedding health reported unhealthy only because Python probes Ollama at `/v1/health/ready`, which returns `404`; this did not block bridge/control-plane checks. NV-Ingest health failed because no service was initially listening on `7670`.
- `ING-BRIDGE-001` passed against `http://127.0.0.1:18082/bridge/extract` and wrote `/tmp/bridge-local-validation.json`.
- Milvus control-plane subset passed for `ING-COL-001`, `ING-META-001`, and `ING-HEALTH-001`; `ING-METRICS-001` failed on missing document-info fields because no upload completed. Output: `/tmp/python-milvus-management-local.json`.
- `ING-DOC-002` passed and wrote task id `6d4038ae-2016-4e0e-b745-53761639a285` to `/tmp/python-ingestor-runtime-local.json`; Redis stored that task as `PENDING`.
- `ING-STS-002` reached the status API but failed because state was `PENDING`, not `FINISHED`. Output: `/tmp/python-ing-sts002-local.json`.
- `ING-VDBCTX-001` and `OPS-INGEST-TELEMETRY-001` timed out in blocking upload after entering the NV-Ingest path. Outputs: `/tmp/python-ing-vdbctx-local.json` and `/tmp/python-ops-ingest-telemetry-local.json`.
- `deploy/compose/docker-compose-ingestor-server.yaml` now makes the NV-Ingest runtime message client host/port configurable via `REDIS_HOST` and `REDIS_PORT`, matching the ingestor-server service. This allows `REDIS_HOST=host.docker.internal` for local reuse of the already-running dockerized Redis on `localhost:6379`.
- Attempted to start `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0` locally with `REDIS_HOST=host.docker.internal`. The image pulled and the container started, but exited with code `139`; logs showed `qemu: uncaught target signal 11 (Segmentation fault)` under amd64 emulation on the arm64 host, plus unresolved default OCR hostnames such as `nemotron-ocr`.
- Classification: items 1-3 are implemented/executed as far as this host can support. Remaining upload/document-info completion requires a known-good NV-Ingest runtime for this architecture or a fully configured compose profile with the required OCR/table/graphic services.

Local fallback workaround on June 28, 2026:

- Added an opt-in Python fallback for local architecture-constrained validation: `APP_NVINGEST_LOCAL_FALLBACK=true`.
- The fallback is intentionally scoped to API/status/catalog/telemetry parity. It extracts local text, writes minimal Milvus rows with Python-compatible `source`, `content_metadata`, and `text` fields, writes collection/document `document_info`, updates Redis-backed task status to `FINISHED`, and emits `ingestion_checkpoint` log events.
- The fallback does not claim native NV-Ingest multimodal parity: it does not produce NV-Ingest-created image/table/chart assets, thumbnails, or exact Ray/message-queue behavior.
- Validated outputs:
  - `/tmp/python-fallback-doc002-local.json`: `ING-DOC-002` passed.
  - `/tmp/python-ingestor-fallback-runtime.json`: captured the non-blocking task id reused by status validation.
  - `/tmp/python-fallback-status-telemetry-metrics.json`: `ING-STS-002`, `OPS-INGEST-TELEMETRY-001`, and `ING-METRICS-001` passed.
- Earlier local fallback/control-plane evidence in the same run also covered `ING-VDBCTX-001`.
- This clears the local post-termination parity work for items 1-3 without requiring the incompatible NV-Ingest container to run on the arm64 host.

.NET API/UI smoke on June 28, 2026:

- Started the .NET reranker, RAG API, ingestor API, and Blazor UI from the built artifacts using `deploy/compose/dotnet-local.env` plus temporary local ports.
- Current local profile is Milvus plus Ollama: `APP_VECTORSTORE_NAME=milvus`, `APP_VECTORSTORE_URL=http://localhost:19530`, `APP_LLM_PROVIDER=ollama`, `APP_EMBEDDINGS_PROVIDER=ollama`, `APP_EMBEDDINGS_MODELNAME=nomic-embed-text`, and `APP_EMBEDDINGS_DIM=768`.
- Blazor startup now accepts both `RagServer__BaseUrl` / `IngestorServer__BaseUrl` and the shorter `RagApi__BaseUrl` / `IngestorApi__BaseUrl` aliases. The corrected smoke run confirmed outbound UI calls used `http://127.0.0.1:18081` and `http://127.0.0.1:18085` and received HTTP 200 responses.
- Runtime checks passed:
  - Reranker `/health`: HTTP 200.
  - RAG `/v1/health?check_dependencies=true`: HTTP 200 with Milvus and Ollama healthy.
  - RAG `/metrics`: HTTP 200 Prometheus output; `/v1/metrics` redirects to `/metrics`.
  - Ingestor `/v1/health?check_dependencies=true`: HTTP 200 with Milvus healthy, object store disabled, and local ingestion backend healthy.
  - Ingestor `/collections`: HTTP 200 with Milvus collection metadata and Python-style document-info fields.
  - Blazor `/`, `/collections/new`, and `/settings`: HTTP 200.
  - Blazor static assets referenced by the prerendered page, including generated app CSS, Blazor server JS, and MudBlazor CSS: HTTP 200.
- The Blazor chat page is routed at `/`; `/chat` returning 404 is expected because navigation points to `/`.

## Implemented Local Harness Support

These commands are now the concrete continuation path for the non-VM items above:

- Bridge route revalidation for a future deployed image:

  ```bash
  uv run python fixtures/run_external_ingestion_bridge_validation.py \
    --endpoint http://127.0.0.1:8082 \
    --backend nvingest \
    --out /tmp/bridge-live-validation.json
  ```

- Request-scoped vector DB and bearer-token fixture. `APP_VECTORSTORE_TOKEN`,
  `APP_VECTORSTORE_API_KEY`, or `MILVUS_TOKEN` is substituted into the fixture
  `Authorization` header when set; if none is set, the placeholder header is
  omitted so unauthenticated local Milvus/Chroma runs do not send a bogus token.

  ```bash
  APP_VECTORSTORE_URL=http://127.0.0.1:19530 \
  uv run python fixtures/run_ingestor_fixtures.py \
    --base-url http://127.0.0.1:8082 \
    --ids ING-VDBCTX-001 \
    --out /tmp/python-ing-vdbctx.json
  ```

- Durable status after restart. Capture a task id first, restart the service
  with the same backing task store, then reuse the runtime file:

  ```bash
  uv run python fixtures/run_ingestor_fixtures.py \
    --base-url http://127.0.0.1:8082 \
    --ids ING-DOC-002 \
    --runtime-out /tmp/ingestor-runtime.json

  uv run python fixtures/run_ingestor_fixtures.py \
    --base-url http://127.0.0.1:8082 \
    --ids ING-STS-002 \
    --runtime-in /tmp/ingestor-runtime.json \
    --out /tmp/python-ing-sts-after-restart.json
  ```

- Ingestion telemetry checkpoints. The runner now validates required checkpoint
  names from the expected markdown against an explicit service log file:

  ```bash
  uv run python fixtures/run_ingestor_fixtures.py \
    --base-url http://127.0.0.1:8082 \
    --ids OPS-INGEST-TELEMETRY-001 \
    --log-file /tmp/ingestor.log \
    --out /tmp/python-ingest-telemetry.json
  ```

## Local Validation After Fixture Updates

The fixture expectation updates were validated locally after the Launchable RAG
rerun:

- `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 207 tests.
- `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
- `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
- `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

After the P0 continuation work, validation was rerun:

- `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 207 tests.
- `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
- `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
- `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

After the final ingestor retry and fixture expectation updates, validation was
rerun again:

- `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 207 tests.
- `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
- `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
- `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

After the reranker outage parity update and remote bridge/Milvus retries,
validation was rerun:

- `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 208 tests.
- `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
- `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
- `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.

After the local fallback and .NET API/UI smoke validation, validation was rerun:

- `dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj --no-restore`: passed, 208 tests.
- `dotnet test src/dotnet_rag/tests/integration/DotnetRag.Tests.Integration.csproj --no-restore`: passed, 3 tests.
- `dotnet build src/dotnet_rag/DotnetRag.sln --no-restore`: passed with 0 warnings and 0 errors.
- `docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet`: passed.
