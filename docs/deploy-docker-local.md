<!--
  SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
  SPDX-License-Identifier: Apache-2.0
-->
# Local Docker Development Without Local NIMs

Use this guide when you want a lightweight local development deployment of the RAG
Blueprint with your own local model servers instead of the NIM containers in
`deploy/compose/nims.yaml`.

This path is intended for development and parity work. It keeps the RAG,
ingestor, vector database, Redis, and object-store services local, but treats
LLM, embedding, VLM, and optional reranking models as external endpoints that
you run separately with a local provider such as Ollama, vLLM, LM Studio,
llama.cpp server, Text Embeddings Inference, or another OpenAI-compatible
server.

:::{note}
The existing Python Dockerfiles currently default to
`nvcr.io/nvidia/base/ubuntu`. This guide removes the local NIM dependency, but
it does not fully remove the Python service image base-image dependency. For a
fully provider-neutral Docker image, add a follow-up Dockerfile/base-image
profile. The .NET Docker Compose path already has first-class Ollama local-dev
support in `deploy/compose/docker-compose-dotnet.yaml`.
:::

## Local model endpoint requirements

The Python RAG stack currently talks to model endpoints through NVIDIA/OpenAI
compatible client libraries:

- LLM: `ChatNVIDIA` with `APP_LLM_SERVERURL`
- Embeddings: `NVIDIAEmbeddings` with `APP_EMBEDDINGS_SERVERURL`
- Reranking: `NVIDIARerank` with `APP_RANKING_SERVERURL`
- VLM generation: OpenAI-compatible chat completions with `APP_VLM_SERVERURL`

For local development, expose your local models through OpenAI-compatible or
NVIDIA-compatible HTTP APIs. Plain Ollama-native `/api/chat` and `/api/embed`
are not a drop-in replacement for the Python compose path unless you use
Ollama's OpenAI-compatible `/v1` API or a compatibility proxy.

Recommended minimum local services:

- Chat completions endpoint, for example `http://localhost:11434/v1`
- Embeddings endpoint, for example `http://localhost:11434/v1`
- Vector database from `deploy/compose/vectordb.yaml`
- Reranker disabled unless you run a compatible reranking endpoint

## Prerequisites

1. Install Docker Engine or Docker Desktop for your operating system.
2. Install Docker Compose v2.
3. Start your local model server before launching the RAG services.
4. Confirm the host can reach your model endpoint.

   ```bash
   curl http://localhost:11434/v1/models
   ```

5. If the RAG containers need to call a model server running on the host, use
   `host.docker.internal` from inside Docker. Docker Desktop provides this name
   automatically. On Linux Docker Engine, add this host mapping to the compose
   services if it is not already available:

   ```yaml
   extra_hosts:
     - "host.docker.internal:host-gateway"
   ```

## Configure local development environments

### Configure .NET local development environment

For the .NET RAG stack, use `deploy/compose/dotnet-local.env` as the primary
local development env file when you run the .NET services from the host, for
example from Visual Studio, VS Code, `dotnet run`, or Aspire. This file points
the .NET services at host-reachable endpoints such as `http://localhost:11434`
and `http://localhost:19530`.

Use `deploy/compose/dotnet-docker.env` only for the full Docker Compose .NET
deployment where the .NET services themselves run as containers. That file is
referenced directly by `deploy/compose/docker-compose-dotnet.yaml` and uses
Docker service DNS names such as `http://ollama:11434`,
`http://chromadb:8000`, and `http://dotnet-reranker-service:8083`.

Recommended split:

- `dotnet-local.env`: source of truth for host-run .NET debugging against local
  Docker infrastructure.
- `dotnet-docker.env`: source of truth for all-container Docker Compose runs.

For host-run .NET development, start only the shared infrastructure:

```bash
docker compose -f deploy/compose/docker-compose-dotnet.yaml up -d chromadb ollama ollama-init
```

Then load `deploy/compose/dotnet-local.env` into your IDE launch profile or
shell before starting the .NET services. Do not use `dotnet-local.env` with
`docker-compose-dotnet.yaml` service containers unless you also rewrite its
`localhost` endpoints to container-reachable names.

For full Docker Compose .NET deployment, let the compose file use
`deploy/compose/dotnet-docker.env`:

```bash
docker compose -f deploy/compose/docker-compose-dotnet.yaml up -d --build
```

### Configure Python local development environment

Create a local shell env file outside version control, for example
`deploy/compose/local-dev.env`, then source it before running compose.

```bash
# Compose files still require these variables for interpolation.
# Local model endpoints may ignore the values.
export NGC_API_KEY=dummy
export NVIDIA_API_KEY=dummy

# Prompt config mounted by the compose files.
export PROMPT_CONFIG_FILE="$(pwd)/src/nvidia_rag/rag_server/prompt.yaml"

# Vector DB. The default compose stack starts Elasticsearch.
# For Milvus, start the milvus profile and change these values.
export APP_VECTORSTORE_NAME=elasticsearch
export APP_VECTORSTORE_URL=http://elasticsearch:9200

# Object store.
export OBJECTSTORE_ENDPOINT=seaweedfs:9010
export OBJECTSTORE_ACCESSKEY=seaweedfsadmin
export OBJECTSTORE_SECRETKEY=seaweedfsadmin

# Local OpenAI-compatible chat model.
export APP_LLM_MODELNAME=qwen2.5:7b
export APP_LLM_SERVERURL=http://host.docker.internal:11434/v1
export APP_LLM_APIKEY=dummy

# Keep auxiliary LLM roles on the same local model unless you need otherwise.
export APP_QUERYREWRITER_MODELNAME="${APP_LLM_MODELNAME}"
export APP_QUERYREWRITER_SERVERURL="${APP_LLM_SERVERURL}"
export APP_QUERYREWRITER_APIKEY=dummy
export ENABLE_QUERYREWRITER=False

export APP_FILTEREXPRESSIONGENERATOR_MODELNAME="${APP_LLM_MODELNAME}"
export APP_FILTEREXPRESSIONGENERATOR_SERVERURL="${APP_LLM_SERVERURL}"
export APP_FILTEREXPRESSIONGENERATOR_APIKEY=dummy
export ENABLE_FILTER_GENERATOR=False

export SUMMARY_LLM="${APP_LLM_MODELNAME}"
export SUMMARY_LLM_SERVERURL="${APP_LLM_SERVERURL}"
export SUMMARY_LLM_APIKEY=dummy

export REFLECTION_LLM="${APP_LLM_MODELNAME}"
export REFLECTION_LLM_SERVERURL="${APP_LLM_SERVERURL}"
export REFLECTION_LLM_APIKEY=dummy
export ENABLE_REFLECTION=False

# Local OpenAI-compatible embedding model.
# Match dimensions to your model.
export APP_EMBEDDINGS_MODELNAME=nomic-embed-text
export APP_EMBEDDINGS_SERVERURL=http://host.docker.internal:11434/v1
export APP_EMBEDDINGS_APIKEY=dummy
export APP_EMBEDDINGS_DIMENSIONS=768

# Reranking is NVIDIA-rerank-client specific in the Python stack.
# Leave disabled unless you run a compatible endpoint.
export ENABLE_RERANKER=False
export APP_RANKING_SERVERURL=
export APP_RANKING_MODELNAME=

# VLM is optional. Enable only when you have a local OpenAI-compatible VLM.
export ENABLE_VLM_INFERENCE=False
export APP_VLM_SERVERURL=http://host.docker.internal:11434/v1
export APP_VLM_MODELNAME=
export APP_VLM_APIKEY=dummy

# Agentic RAG can use the same local chat endpoint, but keep it disabled until
# your local model is strong enough for structured planning JSON.
export ENABLE_AGENTIC_RAG=False
export AGENTIC_PLANNER_LLM_SERVERURL="${APP_LLM_SERVERURL}"
export AGENTIC_PLANNER_LLM_MODEL="${APP_LLM_MODELNAME}"
export AGENTIC_TASK_LLM_SERVERURL="${APP_LLM_SERVERURL}"
export AGENTIC_TASK_LLM_MODEL="${APP_LLM_MODELNAME}"
export AGENTIC_SEED_GEN_LLM_SERVERURL="${APP_LLM_SERVERURL}"
export AGENTIC_SEED_GEN_LLM_MODEL="${APP_LLM_MODELNAME}"
export AGENTIC_SYNTHESIS_LLM_SERVERURL="${APP_LLM_SERVERURL}"
export AGENTIC_SYNTHESIS_LLM_MODEL="${APP_LLM_MODELNAME}"

# Avoid Nemotron-specific reasoning options for generic local models.
export LLM_ENABLE_THINKING=false
export FILTER_THINK_TOKENS=true
```

Then load it:

```bash
source deploy/compose/local-dev.env
```

## Start local infrastructure

Start the vector database and object store. The default profile starts
Elasticsearch and SeaweedFS.

```bash
docker compose -f deploy/compose/vectordb.yaml up -d
```

To use Milvus instead:

```bash
export APP_VECTORSTORE_NAME=milvus
export APP_VECTORSTORE_URL=http://milvus:19530
docker compose -f deploy/compose/vectordb.yaml --profile milvus up -d
```

## Start Python RAG services

For local development from source, prefer building the images from the checkout
instead of pulling prebuilt images:

```bash
docker compose -f deploy/compose/docker-compose-ingestor-server.yaml up -d --build
docker compose -f deploy/compose/docker-compose-rag-server.yaml up -d --build
```

The compose files still include `image: nvcr.io/...` names for release
publishing, but the `build:` sections point at the local checkout. Using
`--build` makes code changes visible in the containers.

:::{warning}
The full Python ingestion stack starts `nv-ingest-ms-runtime`, which is still an
NVIDIA container and expects NV-Ingest-compatible extraction services for full
multimodal ingestion. For provider-neutral local development, use this stack
primarily for RAG/query work against existing data, text-only scenarios, or
mock/fixture-based ingestion until a local ingestion backend is added.
:::

## Health checks

Check the RAG server:

```bash
curl -X GET "http://localhost:8081/v1/health?check_dependencies=false" \
  -H "accept: application/json"
```

For local OpenAI-compatible model servers, dependency health with
`check_dependencies=true` may report model services as unhealthy because the
current Python health checker probes NIM-style `/v1/health/ready` URLs. If
generation works but health fails, use `check_dependencies=false` during local
development or add provider-aware health checks.

Check the ingestor server:

```bash
curl -X GET "http://localhost:8082/v1/health?check_dependencies=false" \
  -H "accept: application/json"
```

## Experiment with the Web User Interface

Open the RAG UI:

```text
http://localhost:8090
```

If upload workflows fail because NV-Ingest is unavailable or not configured for
your local environment, use preloaded vector data, fixtures, or a local text
ingestion path for development.

## Shut down services

```bash
docker compose -f deploy/compose/docker-compose-rag-server.yaml down
docker compose -f deploy/compose/docker-compose-ingestor-server.yaml down
docker compose -f deploy/compose/vectordb.yaml down
```

## Current limitations and recommended follow-up work

- Add explicit Python provider selection, for example
  `APP_LLM_PROVIDER=openai|nvidia|ollama`,
  `APP_EMBEDDINGS_PROVIDER=openai|nvidia|ollama`, and
  `APP_RANKING_PROVIDER=nvidia|disabled`.
- Add provider-aware model health checks:
  NVIDIA/NIM uses `/v1/health/ready`, Ollama uses `/api/tags`, and generic
  OpenAI-compatible servers usually expose `/v1/models`.
- Add a local text ingestion backend that does not require NV-Ingest.
- Add a provider-neutral Python Docker base-image profile if local development
  must avoid the current NVIDIA Ubuntu base images.
- Keep `deploy/compose/nims.yaml` dedicated to NVIDIA NIM deployments instead
  of overloading it for generic local development.

## Related Topics

- [Get Started With the NVIDIA RAG Blueprint](deploy-docker-self-hosted.md)
- [Vector database configuration](change-vectordb.md)
- [RAG Pipeline Debugging Guide](debugging.md)
- [Troubleshoot](troubleshooting.md)
