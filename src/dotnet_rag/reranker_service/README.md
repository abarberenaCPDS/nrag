# Dotnet Reranker Service

Internal ASP.NET Core microservice for chunk reranking used by `dotnet-rag-server`.

## API

- `POST /v1/rerank`
- `POST /rerank`
- `GET /health`

Request contract is shared via `DotnetRag.Shared.Models.RerankerContracts`.

## Providers

- `openai` (OpenAI-compatible `/v1/rerank`)
- `ollama` (embedding similarity via `/api/embed`)
- `lexical` (in-process fallback)

## Configuration

Primary:

- `APP_RANKING_PROVIDER`
- `APP_RANKING_SERVERURL`
- `APP_RANKING_MODELNAME`
- `APP_RANKING_APIKEY`

Fallback:

- `APP_RANKING_FALLBACK_PROVIDER`
- `APP_RANKING_FALLBACK_SERVERURL`
- `APP_RANKING_FALLBACK_MODELNAME`
- `APP_RANKING_FALLBACK_APIKEY`

Other:

- `APP_RANKING_TIMEOUT_SECONDS`
- `APP_RANKING_LEXICAL_EMERGENCY_FALLBACK`
