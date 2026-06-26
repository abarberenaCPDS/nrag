# Models to consider

My take: for local dev, keep the stack intentionally asymmetric. Use a small chat model, a reliable embedding model with a known vector dimension, and do reranking either lexical or embedding-similarity unless you stand up a real local reranker outside Ollama.

**Recommended Local Ollama Profiles**

| Role | Env key(s) | Original NVIDIA model | Local default I’d use | Alternative |
|---|---|---|---|---|
| LLM | `APP_LLM_MODELNAME`, `APP_LLM_PROVIDER`, `APP_LLM_SERVERURL`, `LLM_MAX_TOKENS` | `nvidia/nemotron-3-super-120b-a12b` | `nemotron-3-nano:4b` | `nemotron-mini:4b` or `qwen2.5:3b` |
| Embeddings | `APP_EMBEDDINGS_MODELNAME`, `APP_EMBEDDINGS_PROVIDER`, `APP_EMBEDDINGS_SERVERURL`, `APP_EMBEDDINGS_DIM` | `nvidia/llama-nemotron-embed-vl-1b-v2` | `snowflake-arctic-embed:22m` with dim `384` | `nomic-embed-text` with dim `768` |
| Reranker | `APP_RANKING_PROVIDER`, `APP_RANKING_MODELNAME`, `APP_RANKING_SERVERURL`, `APP_RANKING_FALLBACK_PROVIDER` | `nvidia/llama-nemotron-rerank-1b-v2` | no true Ollama equivalent; use lexical or embedding-similarity | use `nomic-embed-text`/`qwen3-embedding:0.6b` similarity |
| VLM | `ENABLE_VLM_INFERENCE`, `APP_VLM_MODELNAME`, `APP_VLM_SERVERURL`, `VLM_MAX_TOTAL_IMAGES` | `nvidia/nemotron-3-nano-omni-30b-a3b-reasoning` | `minicpm-v` if you want smaller practical VLM | `nemotron3` if you want same NVIDIA family but much heavier |
| Chunking model | `APP_NVINGEST_CHUNKSIZE`, `APP_NVINGEST_CHUNKOVERLAP` | `intfloat/e5-large-unsupervised` | bind chunk sizing to embedding profile | no separate local model needed |

For the LLM, `nemotron-3-nano:4b` is the best family-aligned local choice now. Ollama lists `nemotron-3-nano` with `4b` and `30b` variants, and the `4b` variant is much more appropriate for local dev. `nemotron-mini` is also still a good RAG QA/function-calling local model, but its context is only 4,096 tokens, so `LLM_MAX_TOKENS=4096` is the right ceiling for that profile. ([ollama.com](https://ollama.com/library/nemotron-3-nano))

For embeddings, your current `snowflake-arctic-embed:22m` is the right fast/local default because it is tiny and gives 384-dimensional vectors. `nomic-embed-text` is a better “heavier local default” and is designed for embeddings only; we already know it needs the 768-dim Milvus schema in this repo. Ollama also has `all-minilm` and `qwen3-embedding`, but I’d only use `qwen3-embedding` after adding dimension-profile validation for it. ([ollama.com](https://ollama.com/library/snowflake-arctic-embed?utm_source=openai))

For reranking, I would not pretend Ollama has a drop-in equivalent to `llama-nemotron-rerank-1b-v2`. The current embedding-similarity reranker is acceptable for local dev, but it is not equivalent to a cross-encoder reranker. If we want true local reranking later, I’d add another provider implementation backed by TEI/SentenceTransformers instead of forcing it through Ollama.

For VLM, `nemotron3` is the closest NVIDIA-family option in Ollama, but it is listed as a 33B multimodal model, so it is not what I’d call small local dev. `minicpm-v` is more practical at 8B and explicitly supports multi-image/video understanding; for lightweight dev it is the better default. ([ollama.com](https://ollama.com/library/nemotron3))

**Azure Foundry Options**

For Azure Foundry, I would split this into “Azure Direct” and “NVIDIA-family managed compute/NIM” profiles.

| Role | Env key(s) | Azure Direct practical choice | NVIDIA-family option |
|---|---|---|---|
| LLM | `APP_LLM_MODELNAME`, `APP_LLM_PROVIDER`, `APP_LLM_SERVERURL`, `LLM_MAX_TOKENS` | `gpt-4.1-mini`, `gpt-4.1-nano`, `gpt-5-mini`, `gpt-5-nano` | managed compute model like `nvidia-nemotron-3-nano-30b-a3b-fp8` |
| Embeddings | `APP_EMBEDDINGS_MODELNAME`, `APP_EMBEDDINGS_PROVIDER`, `APP_EMBEDDINGS_SERVERURL`, `APP_EMBEDDINGS_DIM` | `text-embedding-3-small` | Cohere `embed-v-4-0` if multimodal embeddings matter |
| Reranker | `APP_RANKING_PROVIDER`, `APP_RANKING_MODELNAME`, `APP_RANKING_SERVERURL`, `APP_RANKING_FALLBACK_PROVIDER` | `Cohere-rerank-v4.0-fast` | NVIDIA NIM / managed compute if available in catalog |
| VLM | `ENABLE_VLM_INFERENCE`, `APP_VLM_MODELNAME`, `APP_VLM_SERVERURL`, `VLM_MAX_TOTAL_IMAGES` | `gpt-4.1-mini`, `gpt-4.1-nano`, `gpt-4o-mini` | NVIDIA NIM / managed compute for Nemotron Omni if available |

Azure Direct is the most pragmatic for app dev because it is OpenAI-compatible and operationally simpler. Microsoft lists `gpt-4.1-mini` and `gpt-4.1-nano` with text/image input, Chat Completions, Responses API, function calling, and structured outputs. It also lists `gpt-5-mini` and `gpt-5-nano`, but those reasoning models may require code-path care around unsupported generation parameters depending on the model. ([learn.microsoft.com](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure))

For Azure embeddings, `text-embedding-3-small` is the obvious default. It outputs 1,536 dimensions by default and supports configurable dimensions, which is useful if we want a lower-cost vector schema. Cohere `embed-v-4-0` is also interesting because Azure lists it with selectable output dimensions `256`, `512`, `1024`, and `1536`, plus text and image input. ([learn.microsoft.com](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure))

For Azure reranking, `Cohere-rerank-v4.0-fast` is probably the best dev default, and `Cohere-rerank-v4.0-pro` is the quality option. Azure lists both as rerank/text-classification models with Global Standard and managed compute availability. ([learn.microsoft.com](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure))

For NVIDIA-family Azure deployment, Foundry managed compute can deploy Hugging Face catalog models, and Microsoft’s docs use `nvidia-nemotron-3-nano-30b-a3b-fp8` as an example model card. That path is heavier operationally because it requires managed compute quota and accelerator selection, but it is the better parity story for NVIDIA models. Foundry classic also documents NVIDIA NIMs via the Azure Marketplace SaaS offer. ([learn.microsoft.com](https://learn.microsoft.com/en-us/azure/foundry/how-to/deploy-models-managed?tabs=openai-entra&utm_source=openai))

**What I’d Put In Env Profiles**

I’d keep these local profiles:

```env
# Fast local profile
APP_LLM_MODELNAME=nemotron-mini:4b
LLM_MAX_TOKENS=4096
APP_EMBEDDINGS_MODELNAME=snowflake-arctic-embed:22m
APP_EMBEDDINGS_DIM=384
APP_RANKING_PROVIDER=lexical
```

```env
# Better local RAG profile
APP_LLM_MODELNAME=nemotron-3-nano:4b
LLM_MAX_TOKENS=8192
APP_EMBEDDINGS_MODELNAME=nomic-embed-text
APP_EMBEDDINGS_DIM=768
APP_RANKING_PROVIDER=ollama
APP_RANKING_MODELNAME=nomic-embed-text
```

And Azure profiles:

```env
# Azure Direct dev profile
APP_LLM_PROVIDER=openai
APP_LLM_MODELNAME=gpt-4.1-mini
APP_EMBEDDINGS_PROVIDER=openai
APP_EMBEDDINGS_MODELNAME=text-embedding-3-small
APP_EMBEDDINGS_DIM=1536
APP_RANKING_PROVIDER=openai-compatible
APP_RANKING_MODELNAME=Cohere-rerank-v4.0-fast
```

Main warning: changing embedding model or dim means recreating Milvus collections. That should stay loud in the env file and in startup validation.
