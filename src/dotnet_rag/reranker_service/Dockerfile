ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG DOWNLOAD_LEGAL_COMPLIANCE=false

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

COPY src/dotnet_rag/Directory.Build.props src/dotnet_rag/DotnetRag.sln ./src/dotnet_rag/
COPY src/dotnet_rag/utils/DotnetRag.Shared.csproj ./src/dotnet_rag/utils/
COPY src/dotnet_rag/reranker_service/DotnetRag.Reranker_Service.csproj ./src/dotnet_rag/reranker_service/

RUN dotnet restore ./src/dotnet_rag/reranker_service/DotnetRag.Reranker_Service.csproj

COPY src/dotnet_rag ./src/dotnet_rag

RUN dotnet publish ./src/dotnet_rag/reranker_service/DotnetRag.Reranker_Service.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
ARG DOWNLOAD_LEGAL_COMPLIANCE=false

ENV ASPNETCORE_URLS=http://0.0.0.0:8083
WORKDIR /app

COPY --from=build /app/publish ./

RUN useradd --create-home --uid 10001 appuser && \
    chown -R appuser:appuser /app

USER appuser
EXPOSE 8083

ENTRYPOINT ["dotnet", "DotnetRag.Reranker_Service.dll"]
