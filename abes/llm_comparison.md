# LLM Comparison

Python uses dedicated Nemotron NIM services for LLM/embed/rerank; .NET local currently uses Ollama models, with reranking delegated to the new reranker microservice.

```sh
  +----------------------+-----------------------------------------------+-----------------------------------------------+
  | Layer                | Python (pre-migration)                        | .NET (current local migration)                |
  +----------------------+-----------------------------------------------+-----------------------------------------------+
  | App hosts            | rag_server + ingestor_server                  | rag_server + ingestor_server + reranker_svc   |
  | LLM provider         | NVIDIA NIM / NVIDIA API endpoints             | Ollama                                        |
  | LLM model            | nvidia/nemotron-3-super-120b-a12b             | nemotron-mini:latest (dotnet-local.env)       |
  | LLM endpoint         | nim-llm:8000 or integrate.api.nvidia.com/v1   | http://localhost:11434                        |
  | Embedding provider   | NVIDIA NIM / NVIDIA API endpoints             | Ollama                                        |
  | Embedding model      | nvidia/llama-nemotron-embed-vl-1b-v2          | snowflake-arctic-embed:22m                    |
  | Embedding endpoint   | nemotron-vlm-embedding-ms:8000/v1             | http://localhost:11434                        |
  | Reranker provider    | NVIDIA rerank (langchain-nvidia path)         | Dedicated reranker_service + Ollama provider  |
  | Reranker model       | nvidia/llama-nemotron-rerank-1b-v2            | snowflake-arctic-embed:22m                    |
  | Reranker endpoint    | nemotron-ranking-ms:8000                      | http://localhost:8083 (svc), uses :11434      |
  | Vector DB            | Chroma/ES/Milvus (config-dependent)           | Chroma (APP_VECTORSTORE_NAME=chroma)          |
  | Config env file      | .env / nvdev.env / compose envs               | dotnet-local.env (local), dotnet-docker.env   |
  +----------------------+-----------------------------------------------+-----------------------------------------------+
```