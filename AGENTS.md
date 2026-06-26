# NVIDIA RAG Blueprint

Reference implementation for a Retrieval Augmented Generation pipeline. Python 3.11+ backend (FastAPI + LangChain), React/TypeScript frontend, and an active .NET 10 parity implementation with ASP.NET Core services plus Blazor frontend.

## Project structure

```
src/nvidia_rag/
├── rag_server/        # RAG query/response server (FastAPI)
├── ingestor_server/   # Document ingestion server (FastAPI)
└── utils/             # Shared utilities
src/dotnet_rag/
├── rag_server/        # .NET RAG server (ASP.NET Core)
├── ingestor_server/   # .NET ingestion server (ASP.NET Core)
├── reranker_service/  # .NET reranker service
├── blazor_frontend/   # Blazor Server UI
├── utils/             # Shared .NET abstractions/providers
└── tests/             # .NET unit/integration tests
frontend/              # React + TypeScript UI (pnpm)
deploy/
├── compose/           # Docker Compose files and env configs
└── helm/              # Helm charts (standard + MIG-slicing)
docs/                  # User-facing documentation (Sphinx, RST/MD)
tests/
├── unit/              # No network calls allowed
└── integration/       # Network calls permitted
notebooks/             # Jupyter notebooks for evaluation and examples
```

## Development commands

### .NET

```bash
dotnet build src/dotnet_rag/DotnetRag.sln
dotnet test src/dotnet_rag/DotnetRag.sln
dotnet test src/dotnet_rag/DotnetRag.sln --no-restore
dotnet list src/dotnet_rag/DotnetRag.sln package --vulnerable --include-transitive
docker compose -f deploy/compose/docker-compose-dotnet.yaml config --quiet
```

### Backend (Python)

```bash
uv sync                              # Install all deps
# Optional: RAGAS benchmark CLI (see scripts/eval/README.md)
# uv sync --project scripts/eval
uv run pytest tests/unit/            # Unit tests
uv run pytest tests/integration/     # Integration tests
ruff check --fix src/                # Lint + autofix
ruff format src/                     # Format
pre-commit run --all-files           # Run all pre-commit hooks
```

### Frontend (TypeScript)

```bash
cd frontend
pnpm install
pnpm run dev                         # Dev server
pnpm run lint                        # ESLint
pnpm exec tsc --noEmit               # Type check
pnpm run test:run                    # Tests
```

## Code conventions

- **Python**: Ruff for linting and formatting (line-length 88, double quotes, space indent). Config in `pyproject.toml`.
- **Type hints**: Required on all function signatures.
- **Imports**: Sorted by isort via Ruff. No in-function imports.
- **Tests**: Mirror source tree (`src/nvidia_rag/rag_server/server.py` → `tests/unit/rag_server/test_server.py`).
- **.NET**: `net10.0`, nullable enabled, central packages in `src/dotnet_rag/Directory.Packages.props`, xUnit + FluentAssertions + Moq.
- **Frontend**: ESLint + TypeScript strict mode. Function components with hooks.
- **Env files**: `deploy/compose/nvdev.env` (NVIDIA-hosted NIMs) and `deploy/compose/.env` (self-hosted). These are the source of truth for Docker deployments — shell-only exports are lost on restart.

## Current .NET parity state

The .NET parity implementation has advanced significantly. Before changing ingestor or RAG behavior, read:

- `python-analysis.md` — detailed session findings, implementation notes, and runtime blockers.
- `fixtures/parity-dashboard.md` — current fixture status.

Implemented .NET parity features include:

- Interface-selected vector stores: ChromaDB and Milvus.
- Interface-selected embedding/LLM providers: Ollama and OpenAI-compatible endpoints.
- Current Ollama native endpoints: `/api/embed`, `/api/chat`, `/api/tags`.
- Python-compatible metadata schema vocabulary and validation.
- Blazor model/UI sync for snake_case JSON contracts and summary defaults.
- Summary generation/retrieval through provider interfaces, not Chroma-only.
- Milvus schema parity with Python/NV-Ingest canonical fields (`pk`, `vector`, `source`, `content_metadata`, `text`) plus compatibility for .NET fields where present.
- Milvus runtime adaptation: upsert/search/list/delete inspect actual collection fields and do not send fields absent from Python/NV-Ingest collections.
- Request-scoped `vdb_endpoint` and bearer auth for ingestor operations.
- `IIngestionPipeline` boundary for local and external NV-Ingest/NRL bridge extraction.
- Python `POST /bridge/extract` bridge contract with optional real backend flags.
- `IIngestorCatalogStore` for disabled/file-backed catalog persistence.
- Durable task store and queued worker split with `IIngestionJobQueue`.
- Filesystem object-store abstraction for citation/summary artifacts.
- Log-only ingestion telemetry checkpoints for future OTel/DataDog integration.

Important .NET ingestor env vars:

```bash
APP_VECTORSTORE_NAME=chroma|milvus
APP_VECTORSTORE_URL=http://localhost:8000|http://localhost:19530
APP_EMBEDDINGS_PROVIDER=ollama|openai
APP_LLM_PROVIDER=ollama|openai
APP_INGESTION_BACKEND=local|nvingest|nrl
APP_INGESTION_ENDPOINT=http://host:port/bridge/extract
APP_INGESTION_EXECUTION_MODE=queued
APP_INGESTOR_ROLE=api|worker|all
APP_INGESTION_JOB_QUEUE_PATH=/tmp/dotnet-rag-ingestion-jobs
APP_INGESTION_TASK_STORE_PATH=/tmp/dotnet-rag-ingestion-tasks.json
APP_INGESTOR_CATALOG_PATH=/tmp/dotnet-rag-catalog.json
APP_OBJECT_STORE_ROOT=/tmp/dotnet-rag-object-store
```

Python bridge real-backend flags:

```bash
APP_BRIDGE_USE_REAL_NVINGEST=true
APP_BRIDGE_USE_REAL_NRL=true
```

Current local validation containers:

- `abes-milvus` — Milvus REST `19530`, health `9091`
- `abes-redis` — Redis `6379`
- `abes-seaweedfs` — SeaweedFS S3/object store `9010`/`9011`
- `chromadb` — ChromaDB `8000`
- host Ollama — `127.0.0.1:11434`

Current remaining blocker: full Python upload baseline still needs NV-Ingest runtime on `localhost:7670`. Pulling `nvcr.io/nvidia/nemo-microservices/nv-ingest:26.3.0` requires NVCR/NGC access or a pre-pulled image.

Useful parity commands:

```bash
uv run python fixtures/run_python_full_baseline.py \
  --base-url http://127.0.0.1:18097 \
  --out /tmp/python-full-baseline-preflight.json

uv run python fixtures/run_python_full_baseline.py \
  --base-url http://127.0.0.1:18097 \
  --run-fixtures \
  --out /tmp/python-full-baseline.json

APP_INGESTION_ENDPOINT=http://127.0.0.1:18097 \
APP_INGESTION_BACKEND=nvingest \
uv run python fixtures/run_external_ingestion_bridge_validation.py

uv run python fixtures/run_python_rag_mock_fixtures.py \
  --rag-port 18092 \
  --mock-port 18180 \
  --out /tmp/python-rag-mock.json
```

## Deployment modes

1. **Docker Compose** — `deploy/compose/` with env-file configs. Multiple profiles: standard, retrieval-only, NVIDIA-hosted.
2. **Helm** — `deploy/helm/nvidia-blueprint-rag/` chart with `values.yaml`. Supports MIG GPU slicing via `deploy/helm/mig-slicing/`.
3. **Library** — Import `nvidia_rag` as a Python package for custom pipelines.

## Key files

- `pyproject.toml` — All Python deps, ruff config, project metadata
- `deploy/compose/nvdev.env` — Default env file for NVIDIA API Catalog deployments
- `src/nvidia_rag/rag_server/prompt.yaml` — System prompt templates
- `src/nvidia_rag/ingestor_server/server.py` — Python ingestor API and bridge endpoint
- `src/dotnet_rag/DotnetRag.sln` — .NET solution
- `src/dotnet_rag/utils/Extensions/RagInfrastructureExtensions.cs` — shared .NET provider DI
- `src/dotnet_rag/utils/VectorStore/MilvusVectorStore.cs` — Milvus REST provider and schema adaptation
- `src/dotnet_rag/ingestor_server/Services/IngestorService.cs` — .NET ingestor orchestration
- `src/dotnet_rag/ingestor_server/Services/IIngestionPipeline.cs` — ingestion pipeline interface
- `src/dotnet_rag/ingestor_server/Services/IIngestionJobQueue.cs` — queued worker interface
- `src/dotnet_rag/ingestor_server/Services/IIngestorCatalogStore.cs` — catalog persistence interface
- `src/dotnet_rag/blazor_frontend/Models/ApiContracts.cs` — Blazor API models
- `python-analysis.md` — parity findings log
- `fixtures/parity-dashboard.md` — parity status dashboard
- `fixtures/run_ingestor_fixtures.py` — ingestor fixture runner
- `fixtures/run_dotnet_rag_fixtures.py` — isolated .NET RAG fixture runner
- `fixtures/run_python_full_baseline.py` — Python dependency preflight/full baseline runner
- `fixtures/run_external_ingestion_bridge_validation.py` — bridge contract validator
- `docs/support-matrix.md` — GPU requirements per deployment mode
- `docs/service-port-gpu-reference.md` — Port mappings and GPU assignments

## PR and commit guidelines

- Target the `develop` branch, never `main`.
- All commits must be signed off (DCO).
- Run `pre-commit run --all-files` before submitting.
- See `CONTRIBUTING.md` for full workflow.

## Operations — `rag-blueprint` skill

For any operational task — deploying, configuring, troubleshooting, or shutting down the RAG Blueprint — read and follow the repo skill at `skills/rag-blueprint/SKILL.md` (canonical path per the agentskills.io spec) when present. If it is absent in this checkout, use local compose/docs/scripts directly and note that in `python-analysis.md`.

The skill handles:

- **Deploy** — Docker Compose (standard, retrieval-only, NVIDIA-hosted), Helm, MIG-slicing, library mode
- **Configure** — Agentic RAG, VLM, guardrails, query rewriting, ingestion, search & retrieval, models, observability, summarization, reasoning, multimodal, MCP, evaluation, notebooks, UI, and more
- **Troubleshoot** — Debug unhealthy services, container errors, GPU issues, connectivity failures
- **Shutdown** — Stop, tear down, and clean up services
