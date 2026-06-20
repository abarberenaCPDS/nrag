# Dotnet Ingestor Server

ASP.NET Core service for document ingestion, collection management, and task status.

## Build

```bash
dotnet build src/dotnet_rag/ingestor_server/DotnetRag.Ingestor.csproj
```

## Run locally

```bash
ASPNETCORE_URLS=http://0.0.0.0:8082 \
dotnet run --project src/dotnet_rag/ingestor_server/DotnetRag.Ingestor.csproj
```

## Docker

```bash
docker build -f src/dotnet_rag/ingestor_server/Dockerfile -t ingestor-server .
docker run --rm -p 8082:8082 ingestor-server
```

## Swagger / OpenAPI

- Swagger UI: `http://localhost:8082/swagger`
- OpenAPI JSON: `http://localhost:8082/swagger/v1/swagger.json`

## Compose

```bash
cd deploy/compose
docker compose -f docker-compose-ingestor-server.yaml up --build
```
## Testing

If you want, the shortest happy-path is: `POST /collection` first, then `POST /documents`. To test ingestor-server, do this:

1. Start it

```sh
docker compose -f deploy/compose/docker-compose-ingestor-server.yaml up--build```
```

2. Create a collection first

```sh
curl -X POST http://localhost:8082/collection \
-H "Content-Type: application/json" \
-d '{
    "collectionName": "multimodal_data",
    "vdbEndpoint": "http://localhost:19530",
    "description": "test collection",
    "tags": ["test"],
    "owner": "me",
    "createdBy": "me",
    "businessDomain": "test",
    "status": "Active",
    "metadataSchema": []
    }'
```

3. Upload files

```sh
curl -X POST http://localhost:8082/documents \
-H "Authorization: Bearer test-token" \
-F 'data={"collectionName":"multimodal_data","blocking":true,"customMetadata":[],"documentsCatalogMetadata":[],"generateSummary":false,"enablePdfSplitProcessing":false}' \
-F "documents=@/path/to/file1.pdf" \
-F "documents=@/path/to/file2.txt"
```

4. Check status or list docs

```sh
curl "http://localhost:8082/status?task_id=<task_id>"
curl "http://localhost:8082/documents?collection_name=multimodal_data"
```

Swagger:

- http://localhost:8082/swagger
- http://localhost:8082/swagger/v1/swagger.json