# Dotnet RAG Server

ASP.NET Core service for retrieval and answer generation.

## Infrastructure stack

| Layer | Local (default) | Cloud / NIM |
|---|---|---|
| **LLM** | Ollama `qwen2.5:3b` | Any OpenAI-compatible NIM endpoint |
| **Embeddings** | Ollama `nomic-embed-text` | NVIDIA NIM embedding endpoint |
| **Vector DB** | ChromaDB (REST API) | ChromaDB or swap via `IVectorStore` |
| **Chunking** | SK `TextChunker` (512 tok/chunk, 100 tok overlap) | same |

Vector-store capabilities are interface-driven. Milvus opts into generated metadata filter expressions and supplies its schema to the prompt renderer. ChromaDB remains compatible for retrieval and ingestion, and supports simple explicit metadata filters by translating them to Chroma `where` clauses, but it does not opt into generated filter prompts.

Generation, search, and OpenAI-compatible vector-store search requests can override the vector database endpoint with `vdb_endpoint`. When an override endpoint or bearer `Authorization` header is present, the RAG server creates a request-scoped vector-store client through `IVectorStoreClientFactory`; retrieval, query decomposition, and provider-specific filter capabilities all use that selected client. `/v2/vector_stores/{vector_store_id}/search` also accepts `embedding_endpoint` / `embedding_model`, `rewrite_query`, OpenAI-style comparison/compound `filters`, and `reranker_endpoint`.

Generation requests can also override answer-generation endpoints with `llm_endpoint` and VLM endpoints with `vlm_endpoint`. The server creates request-scoped chat clients through `IChatCompletionClientFactory`; blank values keep using the configured singleton providers. `embedding_endpoint` / `embedding_model` create request-scoped embedding clients for retrieval, and `reranker_endpoint` routes reranking to a request-selected reranker service, including the OpenAI-compatible vector-store search route.

Role-specific LLM dependencies are also interface-driven. Query rewriting/query decomposition, filter generation, and reflection use keyed `IChatCompletionService` instances. Set `APP_QUERYREWRITER_SERVERURL` / `APP_QUERYREWRITER_APIKEY`, `APP_FILTEREXPRESSIONGENERATOR_SERVERURL` / `APP_FILTEREXPRESSIONGENERATOR_APIKEY`, or `REFLECTION_LLM_SERVERURL` / `REFLECTION_LLM_APIKEY` to route those tasks to separate OpenAI-compatible endpoints; blank values fall back to the main LLM endpoint and `NVIDIA_API_KEY`. Generation and search requests can override these role dependencies with `query_rewriter_model` / `query_rewriter_endpoint`, `filter_expression_generator_model` / `filter_expression_generator_endpoint`, and `reflection_model` / `reflection_endpoint`; raw API callers may also send the matching `*_api_key` fields. `/configuration` and the Blazor settings UI expose and send the effective role models/endpoints, but configured API keys remain server-side.

### Key env vars

| Variable | Default (Ollama) | Original NIM value |
|---|---|---|
| `APP_LLM_PROVIDER` | `ollama` | `openai` |
| `APP_LLM_MODELNAME` | `qwen2.5:3b` | `nvidia/nemotron-3-super-120b-a12b` |
| `APP_LLM_SERVERURL` | `http://localhost:11434` | `nim-llm:8000` |
| `APP_EMBEDDINGS_MODELNAME` | `nomic-embed-text` | `nvidia/llama-nemotron-embed-vl-1b-v2` |
| `APP_EMBEDDINGS_SERVERURL` | `http://localhost:11434` | `nemotron-vlm-embedding-ms:8000/v1` |
| `APP_VECTORSTORE_NAME` | `chroma` | `elasticsearch` |
| `APP_VECTORSTORE_URL` | `http://localhost:8000` | `http://localhost:9200` |

## Prerequisites (local mode)

```bash
# Pull Ollama models (first run only)
ollama pull qwen2.5:3b            # LLM
ollama pull nomic-embed-text      # Embeddings

# Start ChromaDB
docker run -p 8000:8000 chromadb/chroma
```

## Build

```bash
dotnet build src/dotnet_rag/rag_server/DotnetRag.Rag.csproj
```

## Run locally

```bash
ASPNETCORE_URLS=http://0.0.0.0:8081 \
dotnet run --project src/dotnet_rag/rag_server/DotnetRag.Rag.csproj
```

### Use Ollama as the LLM provider (explicit)

```bash
APP_LLM_PROVIDER=ollama \
APP_LLM_SERVERURL=http://localhost:11434 \
# ORIG_APP_LLM_SERVERURL=nim-llm:8000
APP_LLM_MODELNAME=qwen2.5:3b \
# ORIG_APP_LLM_MODELNAME=nvidia/nemotron-3-super-120b-a12b
APP_EMBEDDINGS_MODELNAME=nomic-embed-text \
# ORIG_APP_EMBEDDINGS_MODELNAME=nvidia/llama-nemotron-embed-vl-1b-v2
APP_VECTORSTORE_URL=http://localhost:8000 \
# ORIG_APP_VECTORSTORE_URL=http://localhost:9200
ASPNETCORE_URLS=http://0.0.0.0:8081 \
dotnet run --project src/dotnet_rag/rag_server/DotnetRag.Rag.csproj
```

## Docker

```bash
docker build -f src/dotnet_rag/rag_server/Dockerfile -t rag-server .
docker run --rm -p 8081:8081 rag-server
```

## Swagger / OpenAPI

- Swagger UI: `http://localhost:8081/swagger`
- OpenAPI JSON: `http://localhost:8081/swagger/v1/swagger.json`
- Versioned docs aliases: `http://localhost:8081/docs`, `/v1/docs`, `/v2/docs`

## Compose

```bash
cd deploy/compose
docker compose -f docker-compose-rag-server.yaml up --build
```

## VS Code debugging on Windows + WSL2

Use the workspace launch profiles from `.vscode/launch.json`:

- `Attach rag_server (.NET 10 in WSL2)` — connect to an already-running process in WSL2.
- `Launch rag_server (.NET 10 in WSL2)` — start and debug `DotnetRag.Rag.dll` on port `8081`.

If launching manually first, run:

```bash
ASPNETCORE_URLS=http://0.0.0.0:8081 \
dotnet run --project src/dotnet_rag/rag_server/DotnetRag.Rag.csproj
```
