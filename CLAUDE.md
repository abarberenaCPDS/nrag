# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

# NVIDIA RAG Blueprint

Reference implementation for a Retrieval Augmented Generation pipeline. Two parallel implementations: Python 3.11+ (FastAPI + LangChain) and .NET 10 (ASP.NET Core), both sharing the same React/TypeScript frontend and a Blazor Server alternative.

## Project structure

```
src/nvidia_rag/          # Python implementation
├── rag_server/          # RAG query/response server (FastAPI)
├── ingestor_server/     # Document ingestion server (FastAPI)
└── utils/               # Shared utilities

src/dotnet_rag/          # .NET 10 implementation
├── rag_server/          # RAG query/response server (ASP.NET Core, port 8081)
├── ingestor_server/     # Document ingestion server (ASP.NET Core, port 8082)
├── reranker_service/    # Reranking microservice (port 8083)
├── blazor_frontend/     # Blazor Server UI (port 5154)
├── utils/               # Shared abstractions, config, vector store, LLM providers
├── AppHost/             # Aspire orchestration
└── tests/               # unit/ and integration/

frontend/                # React + TypeScript UI (pnpm) — source of truth for UI behavior
deploy/
├── compose/             # Docker Compose files and env configs
└── helm/                # Helm charts
docs/                    # Sphinx documentation
tests/                   # Python unit/ and integration/
notebooks/               # Jupyter evaluation notebooks
```

## Development commands

### .NET

```bash
# Build
dotnet build src/dotnet_rag/DotnetRag.sln

# Run all tests
dotnet test src/dotnet_rag/DotnetRag.sln

# Run only unit tests
dotnet test src/dotnet_rag/tests/unit/DotnetRag.Tests.Unit.csproj

# Run a single test class
dotnet test src/dotnet_rag/tests/unit/ --filter "ClassName=InMemoryIngestorStoreTests"

# Run all .NET services locally (loads dotnet-local.env automatically)
scripts/dotnet/run-local-all.sh
```

VS Code: `Ctrl+Shift+B` builds the full solution. `F5` with any launch config also builds first. The `.NET: Full Stack (All Services)` compound launches Blazor + both APIs + Chrome.

### Python

```bash
uv sync                              # Install all deps
uv run pytest tests/unit/            # Unit tests
uv run pytest tests/integration/     # Integration tests
ruff check --fix src/                # Lint + autofix
ruff format src/                     # Format
pre-commit run --all-files           # Run all pre-commit hooks
```

### React frontend

```bash
cd frontend
pnpm install
pnpm run dev                         # Dev server (port 3000)
pnpm run lint
pnpm exec tsc --noEmit
pnpm run test:run
```

## .NET architecture

### Current parity status

The `.NET` implementation has been moved substantially closer to Python API parity. Before changing ingestor/RAG behavior, read:

- `python-analysis.md` — running findings and parity decisions from the migration work.
- `fixtures/parity-dashboard.md` — fixture status and known runtime blockers.

Current hard runtime blocker: full Python upload parity still needs NV-Ingest on `localhost:7670`. Local Redis (`abes-redis`), SeaweedFS (`abes-seaweedfs`), Milvus (`abes-milvus`), ChromaDB, and Ollama have been used for validation, but the NV-Ingest image `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0` requires NVCR/NGC access or a pre-pulled image.

### .NET ingestor catalog/store

The .NET ingestor (`InMemoryIngestorStore`) is no longer just a hard-coded in-memory-only catalog. It is an in-process cache over `IIngestorCatalogStore`:

- `DisabledIngestorCatalogStore` — default true in-memory behavior.
- `FileBackedIngestorCatalogStore` — enabled with `APP_INGESTOR_CATALOG_PATH`.

This preserves .NET-created collection/document metadata across restarts when configured and supports the queued worker split. A future backend-shared catalog should be implemented as another `IIngestorCatalogStore` rather than re-hard-coding persistence into `InMemoryIngestorStore`.

### Service wiring (`utils/`)

`RagInfrastructureExtensions.AddRagInfrastructure()` is called by both `rag_server` and `ingestor_server` to register:
- **LLM provider** — Ollama (`OllamaChatCompletionService`) or OpenAI-compatible NIM (`OpenAiChatCompletionService`) based on `APP_LLM_PROVIDER`
- **Embeddings** — Ollama (`OllamaEmbeddingService`) or OpenAI-compatible (`OpenAiEmbeddingService`) based on `APP_EMBEDDINGS_PROVIDER`
- **Vector store** — `ChromaDbVectorStore` or `MilvusVectorStore` based on `APP_VECTORSTORE_NAME`
- **Summarization** — `SummarizationService` with `SummaryProgressTracker` and rate limiter. It is provider-selected through `IVectorStore`, `IVectorStoreManagement`, and `IVectorDocumentLookup`; do not reintroduce a concrete `ChromaDbVectorStore` dependency.

Current Ollama endpoints are native/current API routes:
- embeddings: `POST /api/embed`
- chat: `POST /api/chat`
- health/model listing: `GET /api/tags`

All services read `RagServerConfiguration` which is populated from environment variables (loaded from `deploy/compose/dotnet-local.env` at startup via `DotnetRagEnvironmentBootstrap`).

### Ingestor pipeline and worker split

The ingestor has an `IIngestionPipeline` boundary:

- `LocalIngestionPipeline` — local dev/default extraction.
- `HttpExternalIngestionPipeline` — calls an external Python/NV-Ingest/NRL bridge.
- `ExternalIngestionPipeline` — fail-fast placeholder when an external backend is selected without an endpoint.

Backend selection:
- `APP_INGESTION_BACKEND=local|nvingest|nrl`
- `APP_NVINGEST_ENDPOINT`, `APP_NRL_ENDPOINT`, or `APP_INGESTION_ENDPOINT`
- optional `APP_INGESTION_API_KEY`

Python exposes `POST /bridge/extract` for this contract. Real bridge execution is opt-in:
- `APP_BRIDGE_USE_REAL_NVINGEST=true`
- `APP_BRIDGE_USE_REAL_NRL=true`

The .NET ingestor also supports queued execution:
- `APP_INGESTION_EXECUTION_MODE=queued`
- `APP_INGESTOR_ROLE=api|worker|all`
- `APP_INGESTION_JOB_QUEUE_PATH`
- `APP_INGESTION_TASK_STORE_PATH`

Docker compose now includes `dotnet-ingestion-worker`, sharing `dotnet-ingestor-data:/tmp-data` with the API service.

### Blazor frontend architecture

**Services** (typed `HttpClient`s, one per downstream API):
- `RagApiService` → port 8081 (generation, summary, config, health)
- `IngestorApiService` → port 8082 (collections, documents, tasks)
- `StreamingService` → port 8081 (SSE chat stream)

**State** (Scoped — one instance per SignalR circuit):
- `ChatState` — messages, streaming, citations, agentic mode
- `CollectionsState` — loaded collections, selected names, drawer open/close
- `SettingsState` — RAG parameters (temperature, top-k, etc.)
- `NotificationState` — toast queue

**Components**: `Components/Pages/` has full pages (`Chat.razor`, `NewCollection.razor`, `Settings.razor`). `Components/Collections/` has the drawer and sidebar. `Components/Chat/` has all chat sub-components.

### API contract rules (Blazor ↔ ingestor)

These apply when writing or reviewing `blazor_frontend/Models/ApiContracts.cs`:

1. **Non-nullable strings, not `string?`** — the ingestor's `CreateCollectionRequest` uses `string` with `= string.Empty` defaults. Blazor must match: send `""` not `null` for unset optional fields. Do not use `string.IsNullOrEmpty(x) ? null : x` when building create requests.

2. **`document_info` vs `metadata` are separate** — `UploadedDocument` has two dictionaries: `metadata` (schema-defined custom fields) and `document_info` (catalog metadata: description, tags, upload_path set at upload time). `DocumentInfo` in `ApiContracts.cs` maps both via `[JsonPropertyName("document_info")] DocumentInfoData`. When reading doc tags/description, check `DocumentInfoData` first.

3. **Metadata type identifiers are Python-compatible** — use canonical values: `"string"`, `"datetime"`, `"number"`, `"integer"`, `"float"`, `"boolean"`, `"array"`. Legacy aliases (`"str"`, `"int"`, `"double"`, `"bool"`) are accepted/normalized by the server for compatibility.

4. **List fields are non-nullable** — `List<string> Tags { get; set; } = []`, not `List<string>?`.

5. **PATCH requests use `null` as "don't update"** — `UpdateCollectionMetadataRequest` fields are `string?` intentionally; `null` means "leave unchanged".

### Collection creation and upload

```
POST /collection  →  create with schema + metadata
POST /documents   →  upload files (optional, separate step)
```

The Blazor UI still uses the create-then-upload flow, but the .NET API now matches Python by auto-creating a missing collection during `POST /documents`.

`NewCollectionForm.GenerateSummary` defaults to `true`; keep the "Generate document summaries" UI enabled by default unless intentionally changing parity.

### Local environment

`deploy/compose/dotnet-local.env` is loaded automatically at startup. Key vars:
- `APP_LLM_PROVIDER` — `ollama` or `openai`
- `APP_VECTORSTORE_NAME` — `chroma` or `milvus`
- `APP_VECTORSTORE_URL` — ChromaDB endpoint (`http://localhost:8000`) or Milvus REST endpoint (`http://localhost:19530`)
- `APP_EMBEDDINGS_SERVERURL` / `APP_LLM_SERVERURL` — Ollama base URL

Start infrastructure only: `docker compose -f deploy/compose/docker-compose-dotnet.yaml up chromadb ollama ollama-init`

Current local parity containers used in validation:
- `abes-milvus` — Milvus REST on `19530`, health on `9091`
- `abes-redis` — Redis on `6379`
- `abes-seaweedfs` — SeaweedFS S3/object store on `9010`/`9011`
- `chromadb` — ChromaDB on `8000` (Docker health may be stale if container predates healthcheck fix)
- host Ollama — `127.0.0.1:11434`

### JSON serialization

Both `rag_server` and `ingestor_server` configure `ConfigureHttpJsonOptions` with `JsonNamingPolicy.SnakeCaseLower` for both property names and dictionary keys. Blazor's `HttpClient` uses default options — `[JsonPropertyName]` attributes on all models handle the mapping explicitly.

## Python code conventions

- **Ruff**: line-length 88, double quotes, space indent. Config in `pyproject.toml`.
- **Type hints**: Required on all function signatures.
- **Imports**: Sorted by isort via Ruff. No in-function imports.
- **Tests**: Mirror source tree (`src/nvidia_rag/rag_server/server.py` → `tests/unit/rag_server/test_server.py`).

## Frontend

- **React**: ESLint + TypeScript strict mode. Function components with hooks. `localhost:3000` is the source of truth for expected UI behavior when building Blazor equivalents.
- **Env files**: `deploy/compose/nvdev.env` (NVIDIA-hosted NIMs) and `deploy/compose/.env` (self-hosted).

## .NET conventions

- Target framework: `net10.0`, `LangVersion: preview`, `Nullable: enable` across all projects (`Directory.Build.props`).
- Package versions are centrally managed in `Directory.Packages.props`.
- Tests use xUnit + FluentAssertions + Moq.
- Blazor state is `Scoped` (per SignalR circuit). Services hitting external APIs are registered as typed `HttpClient`s.
- Timer-based polling (not `Task.Delay` loops) for background state updates in Blazor components; always implement `IDisposable` to cancel timers.

## Key files

- `src/dotnet_rag/utils/Extensions/RagInfrastructureExtensions.cs` — shared DI wiring for LLM/embed/vector stack
- `src/dotnet_rag/utils/Configuration/RagServerConfiguration.cs` — all env var mappings
- `src/dotnet_rag/ingestor_server/Models/Contracts.cs` — ground truth for ingestor server-side types
- `src/dotnet_rag/ingestor_server/Services/InMemoryIngestorStore.cs` — collection/document storage
- `src/dotnet_rag/ingestor_server/Services/IIngestorCatalogStore.cs` — catalog persistence abstraction
- `src/dotnet_rag/ingestor_server/Services/IIngestionPipeline.cs` — local/external ingestion pipeline abstraction
- `src/dotnet_rag/ingestor_server/Services/IIngestionJobQueue.cs` — queued worker abstraction
- `src/dotnet_rag/utils/VectorStore/MilvusVectorStore.cs` — Milvus REST provider; adapts to Python/NV-Ingest canonical schema
- `src/dotnet_rag/blazor_frontend/Models/ApiContracts.cs` — Blazor client-side models (must mirror ingestor contracts)
- `deploy/compose/dotnet-local.env` — local dev environment variables
- `fixtures/run_python_full_baseline.py` — Python dependency preflight and full-baseline runner
- `fixtures/run_external_ingestion_bridge_validation.py` — external bridge contract validator
- `fixtures/run_ingestor_fixtures.py` — ingestor fixture runner
- `fixtures/run_dotnet_rag_fixtures.py` — isolated .NET RAG fixture runner
- `fixtures/run_python_rag_mock_fixtures.py` — Python RAG baseline against mocked OpenAI/NIM-compatible endpoints
- `python-analysis.md` — migration findings log
- `fixtures/parity-dashboard.md` — parity status summary
- `pyproject.toml` — Python deps and ruff config
- `src/nvidia_rag/rag_server/prompt.yaml` — system prompt templates

## Parity validation commands

```bash
# Full .NET verification
dotnet test src/dotnet_rag/DotnetRag.sln --no-restore
dotnet list src/dotnet_rag/DotnetRag.sln package --vulnerable --include-transitive

# Python syntax check for fixture/bridge scripts
uv run python -m py_compile \
  fixtures/run_python_full_baseline.py \
  fixtures/run_external_ingestion_bridge_validation.py \
  fixtures/run_ingestor_fixtures.py \
  src/nvidia_rag/ingestor_server/server.py

# Python full baseline preflight; currently blocks only on NV-Ingest if Redis,
# SeaweedFS, Milvus, Ollama, and Python ingestor are running.
uv run python fixtures/run_python_full_baseline.py \
  --base-url http://127.0.0.1:18097 \
  --out /tmp/python-full-baseline-preflight.json

# Run full Python fixtures only after preflight passes.
uv run python fixtures/run_python_full_baseline.py \
  --base-url http://127.0.0.1:18097 \
  --run-fixtures \
  --out /tmp/python-full-baseline.json
```

## PR and commit guidelines

- Target the `develop` branch, never `main`.
- All commits must be signed off (DCO).
- Run `pre-commit run --all-files` before submitting.
- See `CONTRIBUTING.md` for full workflow.

## Operations — `/rag-blueprint` skill

For any operational task, use the `rag-blueprint` skill (`.agents/skills/rag-blueprint/`).

- **Deploy** — Docker Compose (standard, retrieval-only, NVIDIA-hosted), Helm, MIG-slicing, library mode
- **Configure** — VLM, guardrails, query rewriting, ingestion, search & retrieval, models, observability, summarization, multimodal, MCP, evaluation, notebooks, UI, and more
- **Troubleshoot** — Debug unhealthy services, container errors, GPU issues, connectivity failures
- **Shutdown** — Stop, tear down, and clean up services

## RAG evaluation — `/rag-eval` skill

Filesystem benchmarks (`corpus/` + `train.json` + `scripts/eval/evaluate_rag.py`)

- **Skill:** `skills/rag-eval/SKILL.md` — routing, prerequisites, gotchas (repo root, ingestor base URL without `/v1`, stale collections).
- **References** (under `skills/rag-eval/references/`): `dataset-and-conversion.md`, `benchmark-execution.md` (runs, quality flags, errors, `NVIDIA_API_KEY` hygiene), `evaluate-rag-cli.md`, `result-analysis.md`. Latency/throughput: **rag-perf** skill.
- **Install:** `uv sync --project scripts/eval` — deps live in `scripts/eval/pyproject.toml`.
- **Run** (from repo root): `uv run --project scripts/eval python scripts/eval/evaluate_rag.py --dataset-paths … --host … --port …`. Export **`NVIDIA_API_KEY`** for RAGAS; optional **`RAG_EVAL_JUDGE_MODEL`** (default `mistralai/mixtral-8x22b-instruct-v0.1`).
- **Docs:** dataset contract and README examples — `scripts/eval/README.md`; methodology and notebooks — `docs/evaluate.md`, `notebooks/evaluation_*.ipynb`.
