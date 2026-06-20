This repo is a **reference RAG (Retrieval-Augmented Generation) platform**. It’s a monorepo for building a document-ingestion + search + answer-generation system, with both a **Python backend** and a **React frontend**, plus deployment and evaluation tooling.

**Big picture flow:** documents go into the **ingestor server**, are parsed/chunked/indexed into a vector store, then the **RAG server** handles user queries by retrieving relevant context, optionally reranking/reflection/guardrails, and generating grounded answers. The frontend is a chat-style UI that talks to the RAG API.

**Main pieces:**
- `src/nvidia_rag/ingestor_server/` — document ingestion APIs and task handling
- `src/nvidia_rag/rag_server/` — query/response API, prompt logic, reflection, validation, tracing, VLM support
- `src/nvidia_rag/utils/` — shared helpers for config, embeddings, LLMs, vector store, reranking, object storage
- `frontend/` — TypeScript UI for chatting with the system
- `deploy/compose/` and `deploy/helm/` — Docker Compose and Kubernetes/Helm deployment paths
- `tests/unit/` and `tests/integration/` — isolated unit tests vs. service-backed integration tests
- `skills/` — assistant-specific operational workflows for deploying, tuning, evaluating, and troubleshooting the blueprint

It’s designed around **NVIDIA NIM microservices** and supports multiple modes: local/self-hosted, NVIDIA-hosted endpoints, and Kubernetes/Helm deployments. The repo also includes **evaluation tooling** for RAG quality and performance, which is a big part of the project’s workflow.

If you want, I can also turn this into a **component-by-component map** of the codebase or create the `.github/copilot-instructions.md` file you originally asked for.