# Future OpenAI and Azure Foundry Implementation

## Goal

Make local .NET RAG development provider-agnostic so Ollama remains the default
debug baseline, while OpenAI and Azure Foundry can be used intentionally to test
provider behavior without pulling in NVIDIA-hosted model profiles.

The intent is to debug code paths, prompts, retrieval, streaming, role handling,
and UI behavior. It is not to optimize for model quality.

## Current State

- `dotnet-local.env` is the default local development profile loaded by
  `DotnetRagEnvironmentBootstrap`.
- Ollama is the cleanest default because it keeps LLM, embeddings, reranking,
  and VLM endpoints local and repeatable.
- The .NET OpenAI-compatible chat and embedding clients currently use
  `NVIDIA_API_KEY` as the bearer token source.
- The OpenAI-compatible provider normalizes endpoints as:
  - chat: `<base>/v1/chat/completions`
  - embeddings: `<base>/v1/embeddings`
  - rerank: `<base>/v1/rerank`
- This means OpenAI-compatible `/v1` endpoints work, but classic Azure OpenAI
  deployment URLs with `api-version` are not first-class yet.

## Desired Provider Model

Keep `APP_*_PROVIDER=ollama|openai` as the simple public switch, but stop using
`NVIDIA_API_KEY` as the generic API key for every OpenAI-compatible provider.

Add provider-specific API key support:

```env
APP_LLM_APIKEY=
APP_EMBEDDINGS_APIKEY=
APP_RANKING_APIKEY=
APP_QUERYREWRITER_APIKEY=
APP_FILTEREXPRESSIONGENERATOR_APIKEY=
APP_VLM_APIKEY=
REFLECTION_LLM_APIKEY=
```

Recommended precedence:

1. Request-scoped API key field, where a request contract already supports it.
2. Role-specific env var, such as `APP_QUERYREWRITER_APIKEY`.
3. Provider-level env var, such as `APP_LLM_APIKEY`.
4. Compatibility fallback to `NVIDIA_API_KEY`.
5. Compatibility fallback to `OPENAI_API_KEY`.

## OpenAI Support

OpenAI can use the existing OpenAI-compatible wire format:

```env
APP_LLM_PROVIDER=openai
APP_LLM_SERVERURL=https://api.openai.com/v1
APP_LLM_MODELNAME=gpt-4.1-mini
APP_LLM_APIKEY=<openai-api-key>
```

Implementation work:

- Update `RagServerConfiguration` to expose LLM, embedding, VLM, ranking, and
  role-specific API keys.
- Update `RagInfrastructureExtensions` to pass those keys into
  `OpenAiChatCompletionService` and `OpenAiEmbeddingService`.
- Update `RerankerServiceConfiguration` to prefer `APP_RANKING_APIKEY` and keep
  current fallbacks.
- Update `/v1/configuration` to show which provider family is configured, but
  never return API key values.

## Azure Foundry Support

Support Azure Foundry in two phases.

Phase 1: OpenAI-compatible Foundry endpoints.

```env
APP_LLM_PROVIDER=openai
APP_LLM_SERVERURL=https://<foundry-endpoint>/v1
APP_LLM_MODELNAME=<model-or-deployment-name>
APP_LLM_APIKEY=<foundry-key>
```

This should work with the existing OpenAI-compatible client once
provider-specific API keys are wired.

Phase 2: Azure OpenAI deployment URLs.

Classic Azure OpenAI endpoints usually require deployment-specific paths and an
`api-version` query parameter. Do not force that into the current
OpenAI-compatible endpoint normalizer. Add a dedicated provider if needed:

```env
APP_LLM_PROVIDER=azure-openai
APP_LLM_SERVERURL=https://<resource>.openai.azure.com
APP_LLM_MODELNAME=<deployment-name>
APP_LLM_APIKEY=<azure-openai-key>
APP_LLM_API_VERSION=2025-01-01-preview
```

Implementation work:

- Add an Azure-specific chat client only if Foundry/OpenAI-compatible `/v1`
  endpoints are insufficient.
- Keep the existing OpenAI-compatible provider unchanged for `/v1` services.
- Add tests for endpoint normalization so `/v1` and Azure deployment URL behavior
  cannot regress silently.

## Local Debugging Defaults

For most local debugging, keep:

```env
APP_LLM_PROVIDER=ollama
APP_EMBEDDINGS_PROVIDER=ollama
APP_RANKING_PROVIDER=ollama
APP_VECTORSTORE_NAME=milvus
APP_VECTORSTORE_URL=http://localhost:19530
APP_EMBEDDINGS_MODELNAME=nomic-embed-text
APP_EMBEDDINGS_DIM=768
```

When testing OpenAI or Azure Foundry behavior, switch only the LLM first. Keep
embeddings and reranking local unless the test specifically targets provider
integration. This avoids vector collection churn and keeps failures easier to
interpret.

## Test Coverage To Add

- Unit tests for API key precedence in chat, embeddings, reranker, query
  rewriting, filter generation, reflection, and VLM.
- Unit tests for OpenAI-compatible endpoint normalization.
- Unit tests for Azure-specific endpoint construction if an Azure provider is
  added.
- Integration smoke fixture profiles for:
  - Ollama local baseline.
  - OpenAI chat-only with local embeddings/reranker.
  - Azure Foundry chat-only with local embeddings/reranker.
- Configuration endpoint tests that assert API keys are not exposed.
