# dotnet_rag scaffold

This tree is the C#/.NET 10 migration target for `src/nvidia_rag`.

## Layout

- `ingestor_server/` — ingestion API and background-processing host
- `rag_server/` — retrieval and answer-generation host
- `utils/` — shared contracts, options, and future service adapters

## Current scaffold

- ASP.NET Core service shells for the ingestor and RAG hosts
- Shared abstractions for chat, embeddings, vector search, and runtime options
- Local-first config stubs for Ollama, ChromaDB, and telemetry
