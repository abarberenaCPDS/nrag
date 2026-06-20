# Dotnet RAG Server

ASP.NET Core service for retrieval and answer generation.

## Infrastructure stack

| Layer | Local (default) | Cloud / NIM |
|---|---|---|
| **LLM** | Ollama `qwen2.5:3b` | Any OpenAI-compatible NIM endpoint |
| **Embeddings** | Ollama `nomic-embed-text` | NVIDIA NIM embedding endpoint |
| **Vector DB** | ChromaDB (REST API) | ChromaDB or swap via `IVectorStore` |
| **Chunking** | SK `TextChunker` (512 tok/chunk, 100 tok overlap) | same |

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
