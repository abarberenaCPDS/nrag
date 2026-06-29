# dotnet_rag scaffold

This tree is the C#/.NET 10 migration target for `src/nvidia_rag`.

## Layout

- `ingestor_server/` — ingestion API and background-processing host
- `rag_server/` — retrieval and answer-generation host
- `reranker_service/` — internal reranking microservice consumed by `rag_server`
- `utils/` — shared contracts, options, and service adapters

## Current scaffold

- ASP.NET Core service shells for the ingestor, RAG, and reranker hosts
- Shared abstractions for chat, embeddings, vector search, and rerank contracts
- Local-first config stubs for Ollama, ChromaDB, Milvus, and telemetry
- Blazor Server UI wired to the same request contracts and feature toggles as the .NET RAG server

## Provider boundaries

- Vector database behavior is selected through shared interfaces. ChromaDB and Milvus are concrete implementations behind `IVectorStore`, `IVectorStoreManagement`, and related provider contracts.
- Backend-specific filter support belongs in the vector store implementation. Milvus supports generated metadata filter expressions through `IVectorStoreFilterCapabilities`; ChromaDB does not opt into generated filters, but it translates simple explicit metadata filter expressions into native Chroma `where` clauses.
- The Blazor settings UI sends feature flags such as query decomposition and filter generation to the server. The server decides whether a selected provider can execute that feature, so UI state stays synchronized without hard-coding provider behavior into chat requests.

## Local dev config unification

- All three services call a shared bootstrap (`DotnetRagEnvironmentBootstrap`) during startup.
- Bootstrap loads `deploy/compose/dotnet-local.env` automatically (or `DOTNET_RAG_ENV_FILE` if set).
- Existing process env vars still win over file values.
- Set `DOTNET_RAG_SKIP_ENV_BOOTSTRAP=true` to disable this behavior.

### Start all three services with one command

```bash
scripts/dotnet/run-local-all.sh
```
