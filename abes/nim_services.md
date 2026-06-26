# NIM Microservices comparison

Yes. For the **NIM services in `README.md`**, I’d split them into three buckets for .NET:

1. **Can use from .NET now with OpenAI-compatible providers**
2. **Can use from .NET after small provider fixes**
3. **Should stay behind NV-Ingest / NeMo Retriever bridge**

**NIM Mapping**

| README NIM | Purpose | .NET fit | Recommendation |
|---|---|---|---|
| `nvidia/nemotron-3-super-120b-a12b` | main LLM | Works with current `OpenAiChatCompletionService` | Use `APP_LLM_PROVIDER=openai`, `APP_LLM_SERVERURL=https://integrate.api.nvidia.com/v1` |
| `nvidia/llama-nemotron-embed-1b-v2` | text embeddings | Mostly works, but needs dimensions handling | Add `dimensions` support before using lower dims like `384`/`768` |
| `nvidia/llama-nemotron-embed-vl-1b-v2` | multimodal embeddings | Text-only path may work; image payloads do not | Use via NV-Ingest bridge for multimodal parity |
| `nvidia/llama-nemotron-rerank-1b-v2` | text reranking | Needs endpoint/payload normalization | Add NVIDIA rerank provider or extend current OpenAI-compatible reranker |
| `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning` | VLM generation | Likely works as OpenAI-compatible chat if payload is text/image compatible | Good optional VLM profile, but validate image message format |
| `nvidia/nemotron-nano-12b-v2-vl` | captioning / VLM | Not directly wired for ingestion captions in .NET | Keep behind NV-Ingest bridge first |
| Page/table/graphic/OCR NIMs | document extraction | Not direct RAG server providers | Keep behind NV-Ingest / NRL bridge |
| `nvidia/nemotron-parse` | document parsing | Not direct vector/RAG provider | Keep behind NV-Ingest / NRL bridge |
| NemoGuard NIMs | guardrails | Not currently equivalent in .NET | Later: separate guardrail provider/interface |

The README lists the core NIM set under “NVIDIA NIM Microservices”, including the main Nemotron LLM, text embedding/reranking, extraction NIMs, VLM, parse, OCR, and VL embed/rerank models in [README.md](/Users/abe/src/nvidia/abes-rag/README.md:109).

**Most Important .NET Gap**

For NVIDIA embeddings, do not assume the dimension unless the request controls it. NVIDIA’s `llama-nemotron-embed-1b-v2` supports configurable output dimensions of `384`, `512`, `768`, `1024`, or `2048`; the model card also describes the base embedding size as `2048`. Our current `.NET` `OpenAiEmbeddingService` only sends `model` and `input`, not `dimensions`, so `APP_EMBEDDINGS_DIM` may need to be `2048` unless we add a `dimensions` request parameter. ([build.nvidia.com](https://build.nvidia.com/nvidia/llama-nemotron-embed-1b-v2/modelcard?utm_source=openai))

So I’d add:

```env
APP_EMBEDDINGS_DIM=2048
APP_EMBEDDINGS_REQUEST_DIMENSIONS=2048
```

Or, if we want NIM embeddings to match local Snowflake/Nomic profiles:

```env
APP_EMBEDDINGS_MODELNAME=nvidia/llama-nemotron-embed-1b-v2
APP_EMBEDDINGS_DIM=768
APP_EMBEDDINGS_REQUEST_DIMENSIONS=768
```

Then update `OpenAiEmbeddingService` to send:

```json
{
  "model": "...",
  "input": "...",
  "dimensions": 768
}
```

**Reranker Gap**

NVIDIA reranking is a true cross-encoder reranker, which is better parity than our local Ollama embedding-similarity fallback. NVIDIA describes `llama-nemotron-rerank-1b-v2` as a NeMo Retriever reranking model for multilingual/cross-lingual text QA retrieval, and its NGC page describes it as producing relevance scores for query/document relevance. ([huggingface.co](https://huggingface.co/nvidia/llama-nemotron-rerank-1b-v2?utm_source=openai))

But our current `.NET` reranker provider normalizes to `/v1/rerank`. The Python/NVIDIA env comments point to endpoints like:

```env
https://ai.api.nvidia.com/v1/retrieval/nvidia/llama-nemotron-rerank-1b-v2/reranking
```

So for .NET I would add an explicit `NvidiaRerankerProvider` instead of overloading the OpenAI-compatible provider. It should preserve full URLs ending in `/reranking`, support NVIDIA’s expected request shape, and parse NVIDIA’s response shape.

**Recommended NVIDIA Hosted NIM .NET Profile**

```env
NVIDIA_API_KEY=...

APP_LLM_PROVIDER=openai
APP_LLM_SERVERURL=https://integrate.api.nvidia.com/v1
APP_LLM_MODELNAME=nvidia/nemotron-3-super-120b-a12b
LLM_TEMPERATURE=0.0
LLM_TOP_P=1.0
LLM_MAX_TOKENS=16256

APP_EMBEDDINGS_PROVIDER=openai
APP_EMBEDDINGS_SERVERURL=https://integrate.api.nvidia.com/v1
APP_EMBEDDINGS_MODELNAME=nvidia/llama-nemotron-embed-1b-v2
APP_EMBEDDINGS_DIM=2048
# Future after code change:
# APP_EMBEDDINGS_REQUEST_DIMENSIONS=2048

APP_RANKING_PROVIDER=nvidia
APP_RANKING_SERVERURL=https://ai.api.nvidia.com/v1/retrieval/nvidia/llama-nemotron-rerank-1b-v2/reranking
APP_RANKING_MODELNAME=nvidia/llama-nemotron-rerank-1b-v2
APP_RANKING_FALLBACK_PROVIDER=lexical
```

For multimodal retrieval parity:

```env
APP_EMBEDDINGS_MODELNAME=nvidia/llama-nemotron-embed-vl-1b-v2
APP_EMBEDDINGS_DIM=2048
```

But I would only use that through the ingestion bridge until .NET supports image/document payload embeddings. The VL embedding model supports text, image, and combined image/text document embeddings for multimodal QA retrieval; the model outputs embeddings up to dimension `2048`. ([docs.api.nvidia.com](https://docs.api.nvidia.com/nim/reference/nvidia-llama-nemotron-embed-vl-1b-v2?utm_source=openai))

**Extraction NIMs**

For these:

- `nemotron-page-elements-v3`
- `nemotron-table-structure-v1`
- `nemotron-graphic-elements-v1`
- `nemotron-ocr`
- `nemotron-parse`
- `paddleocr`

I would not implement direct .NET clients yet. They belong behind `APP_INGESTION_BACKEND=nvingest` / `nrl` because the orchestration is more than one HTTP call: page splitting, OCR, table/chart extraction, image captioning, metadata, chunking, and storage. NVIDIA describes Page Elements as object detection for charts/tables/titles, OCR’s `/v1/infer` as accepting base64 images and returning text detections with bounding boxes/confidence, and Nemotron Parse as document text/metadata extraction. ([build.nvidia.com](https://build.nvidia.com/nvidia/nemotron-page-elements-v3?utm_source=openai))

**Azure Foundry NIM Options**

For Azure Foundry, the closest catalog equivalents I’d look at are:

| Purpose | Azure Foundry NIM-style model |
|---|---|
| Embeddings | `Llama-3.2-NV-embedqa-1b-v2-NIM-microservice` |
| Reranking | `Llama-3.2-NV-rerankqa-1b-v2-NIM-microservice` |
| Parsing | `NVIDIA-Nemotron-Parse-NIM-microservice` |

Azure’s catalog describes the embedding NIM microservice as a NeMo Retriever Llama 3.2 1B embedding model with long-document support up to 8192 tokens and dynamic Matryoshka embedding sizing. It describes the reranker NIM microservice as producing relevance scores for query/document relevance, also with long-document support up to 8192 tokens. ([ai.azure.com](https://ai.azure.com/catalog/models/Llama-3.2-NV-embedqa-1b-v2-NIM-microservice?utm_source=openai))

**My Suggested Next Implementation Items**

1. Add `APP_EMBEDDINGS_REQUEST_DIMENSIONS` and send `dimensions` from `OpenAiEmbeddingService`.
2. Add known NIM embedding validation:
   - `nvidia/llama-nemotron-embed-1b-v2`: allow `384|512|768|1024|2048`
   - `nvidia/llama-nemotron-embed-vl-1b-v2`: default/validate `2048`
3. Add `NvidiaRerankerProvider` for `/reranking` endpoints.
4. Keep extraction NIMs behind the existing ingestion bridge.
5. Add `dotnet-nvidia-hosted.env` as a clean profile separate from Ollama local profiles.