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

### Key design decision: .NET ingestor has its own in-memory store

The .NET ingestor (`InMemoryIngestorStore`) is **not** a proxy to the Python ingestor. It maintains its own in-memory collection/document state. Collections created via the React frontend (Python API) are invisible to the Blazor frontend (.NET ingestor), and vice versa. ChromaDB is shared for vector search, but collection metadata is separate.

### Service wiring (`utils/`)

`RagInfrastructureExtensions.AddRagInfrastructure()` is called by both `rag_server` and `ingestor_server` to register:
- **LLM provider** — Ollama (`OllamaChatCompletionService`) or OpenAI-compatible NIM (`OpenAiChatCompletionService`) based on `APP_LLM_PROVIDER`
- **Embeddings** — `OllamaEmbeddingService` (defaults to `nomic-embed-text` locally)
- **Vector store** — `ChromaDbVectorStore` or `MilvusVectorStore` based on `APP_VECTORSTORE_NAME`
- **Summarization** — `SummarizationService` with `SummaryProgressTracker` and rate limiter

All services read `RagServerConfiguration` which is populated from environment variables (loaded from `deploy/compose/dotnet-local.env` at startup via `DotnetRagEnvironmentBootstrap`).

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

3. **Type identifier is `"str"` not `"string"`** — `MetadataFieldDef.Type` defaults to `"str"`. Valid values: `"str"`, `"int"`, `"float"`, `"bool"`.

4. **List fields are non-nullable** — `List<string> Tags { get; set; } = []`, not `List<string>?`.

5. **PATCH requests use `null` as "don't update"** — `UpdateCollectionMetadataRequest` fields are `string?` intentionally; `null` means "leave unchanged".

### Two-step collection creation

```
POST /collection  →  create with schema + metadata
POST /documents   →  upload files (optional, separate step)
```

The collection must exist before documents can be uploaded. Do not skip the first step.

### Local environment

`deploy/compose/dotnet-local.env` is loaded automatically at startup. Key vars:
- `APP_LLM_PROVIDER` — `ollama` or `openai`
- `APP_VECTORSTORE_NAME` — `chroma` (default) or `milvus`
- `APP_VECTORSTORE_URL` — ChromaDB endpoint (default `http://localhost:8000`)
- `APP_EMBEDDINGS_SERVERURL` / `APP_LLM_SERVERURL` — Ollama base URL

Start infrastructure only: `docker compose -f deploy/compose/docker-compose-dotnet.yaml up chromadb ollama ollama-init`

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
- `src/dotnet_rag/blazor_frontend/Models/ApiContracts.cs` — Blazor client-side models (must mirror ingestor contracts)
- `deploy/compose/dotnet-local.env` — local dev environment variables
- `pyproject.toml` — Python deps and ruff config
- `src/nvidia_rag/rag_server/prompt.yaml` — system prompt templates

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
