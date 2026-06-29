# Findings from Python Notebook

Reviewed read-only.

`DocumentUploadRequest` in [Contracts.cs](/Users/abe/src/nvidia/abes-rag/src/dotnet_rag/ingestor_server/Models/Contracts.cs:34) mostly matches the current Python FastAPI `DocumentUploadRequest` shape for upload JSON, and .NET deserialization does use snake_case via `JsonNamingPolicy.SnakeCaseLower` in [MultipartRequestExtensions.cs](/Users/abe/src/nvidia/abes-rag/src/dotnet_rag/ingestor_server/MultipartRequestExtensions.cs:7).

**Matches / works**
- `vdb_endpoint` -> `VdbEndpoint`
- `collection_name` -> `CollectionName`
- `blocking` -> `Blocking`
- `split_options.chunk_size` -> `SplitOptions.ChunkSize`
- `split_options.chunk_overlap` -> `SplitOptions.ChunkOverlap`
- `custom_metadata` -> `CustomMetadata`
- `generate_summary` -> `GenerateSummary`
- `documents_catalog_metadata` -> `DocumentsCatalogMetadata`
- `summary_options` -> `SummaryOptions`
- `enable_pdf_split_processing` -> `EnablePdfSplitProcessing`
- `pdf_split_processing_options.pages_per_chunk` -> `PdfSplitProcessingOptions.PagesPerChunk`

**Likely missing**
- `extraction_options` is not present in .NET `DocumentUploadRequest`.
- There is no .NET model for:
  - `extract_text`
  - `extract_tables`
  - `extract_charts`
  - `extract_images`
  - `extract_method`
  - `text_depth`

So your example JSON would deserialize without failing, but `extraction_options` would be ignored unless the JSON options are configured to reject unknown properties, which they are not.

**Other mismatch**
- .NET `DocumentCatalogMetadata` includes `Tags`; current Python `DocumentCatalogMetadata` shown in `server.py` only has `filename` and `description`. This is extra on the .NET side, not harmful for .NET, but not exact Python request parity.

**Blazor**
Blazor upload currently sends only:
- `collection_name`
- `blocking`
- `generate_summary`
- `documents_catalog_metadata`

It does not send `vdb_endpoint`, `split_options`, `summary_options`, PDF split options, custom metadata, or extraction options from the upload path I inspected.

Bottom line: snake_case serialization is OK, but `extraction_options` is missing from the .NET contract and Blazor upload surface.