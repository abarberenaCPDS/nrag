# Missing

ingestor server

```text
+---------------------------------------------------------------+---------------------------------------------------------------+-------------------------------+
| Python import                                                  | Current .NET counterpart                                      | Status                        |
+---------------------------------------------------------------+---------------------------------------------------------------+-------------------------------+
| _DEFAULT_EXTRACTOR_MAP                                         | None                                                          | Missing: no NV-Ingest extract |
| EXTENSION_TO_DOCUMENT_TYPE                                     | IngestorService.UnsupportedFormats                            | Partial: hard-coded inverse   |
| VDB                                                           | IVectorStore                                                  | Partial: search/upsert only   |
| MilvusClient                                                   | MilvusVectorStore                                             | Partial: REST wrapper         |
| IngestionStateManager                                          | IngestionTaskHandler + IngestionTaskStatusResponse            | Partial                       |
| get_nv_ingest_client                                           | None                                                          | Missing                       |
| get_nv_ingest_ingestor                                         | IngestorService.IngestIntoVectorStoreAsync                    | Partial/native replacement    |
| INGESTION_TASK_HANDLER                                         | IngestionTaskHandler                                          | Partial: in-memory only       |
| APIError                                                       | IResult/Results + normal Exceptions                           | Partial: no shared APIError   |
| configure_object_store_operator                                | None                                                          | Missing                       |
| calculate_dynamic_batch_parameters                             | BatchUtilities.CalculateDynamicBatchParameters                | Present                       |
| create_catalog_metadata                                        | InMemoryIngestorStore.CreateCollection                        | Partial                       |
| create_document_metadata                                       | IngestorService documentInfo dictionary                       | Partial/minimal               |
| derive_boolean_flags                                           | None                                                          | Missing                       |
| get_current_timestamp                                          | DateTimeOffset.UtcNow                                         | Ad hoc                        |
| perform_document_info_aggregation                              | None                                                          | Missing                       |
| NvidiaRAGConfig                                                | RagServerConfiguration                                        | Present                       |
| IngestorHealthResponse                                         | DotnetRag.Ingestor.Models.IngestorHealthResponse              | Present                       |
| get_prompts                                                    | SummarizationPrompts.Load                                     | Partial: summarization only   |
| SYSTEM_MANAGED_FIELDS                                          | None                                                          | Missing                       |
| MetadataField                                                  | DotnetRag.Ingestor.Models.MetadataField                       | Partial: different type names |
| MetadataSchema                                                 | List<MetadataField> on CreateCollectionRequest                | Partial                       |
| MetadataValidator                                              | InMemoryIngestorStore.ValidateDocumentMetadata                | Partial/basic                 |
| DEFAULT_BUCKET_NAME                                            | None                                                          | Missing                       |
| get_object_store_operator                                      | None                                                          | Missing                       |
| get_unique_thumbnail_id_collection_prefix                      | None                                                          | Missing                       |
| get_unique_thumbnail_id_file_name_prefix                       | None                                                          | Missing                       |
| create_nv_ingest_trace_context                                 | None                                                          | Missing                       |
| get_tracer                                                     | OpenTelemetry via ObservabilityExtensions                     | Partial                       |
| process_nv_ingest_traces                                       | None                                                          | Missing                       |
| trace_function                                                 | OpenTelemetry auto/instrumentation                            | Partial/no decorator analog   |
| generate_document_summaries                                    | ISummarizationService.GenerateDocumentSummariesAsync          | Present/partial storage       |
| SUMMARY_STATUS_HANDLER                                         | SummaryProgressTracker                                        | Partial: in-memory only       |
| DEFAULT_DOCUMENT_INFO_COLLECTION                               | None                                                          | Missing                       |
| _get_vdb_op                                                    | DI registration in RagInfrastructureExtensions                | Partial                       |
| VDBRag                                                         | IVectorStore + ChromaDbVectorStore/MilvusVectorStore          | Partial                       |
| SerializedVDBWrapper                                           | None                                                          | Missing                       |
+---------------------------------------------------------------+---------------------------------------------------------------+-------------------------------+
```