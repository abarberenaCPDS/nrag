# Video Ingestion Failure Findings

## Summary

The `video` collection upload failed because NV-Ingest tried to run audio/video transcription through the Riva ASR service at `audio:50051`, but no `audio` container/service was running or resolvable on the Docker network.

The ingestor surfaced this as:

```text
NV-Ingest ingestion failed with no results.
```

The lower-level NV-Ingest runtime error was:

```text
DNS resolution failed for audio:50051
```

This was not a vector DB write failure, object-store failure, or collection schema issue. The MP4 extraction completed as a nominal NV-Ingest batch with no client-side failures, but produced zero extraction items because the audio extractor could not reach its ASR backend.

## Evidence

Remote task:

```text
task_id=960a0316-3c66-4a4f-a4ef-44f6786615fa
collection_name=video
file=/tmp-data/uploaded_files/video/Seattle Seahawks vs New England Patriots - Super Bowl LX Game Highlights.mp4
```

`ingestor-server` log sequence:

```text
2026-06-28T00:30:04Z Performing ingestion in collection_name: video
2026-06-28T00:30:04Z === Processing Batch - Collection: video - Batch 1 of 1 - Documents in batch: 1 ===
2026-06-28T00:30:04Z Post chunk split config: enable_paged_doc_split=False. Splitting by: ['text', 'html', 'mp3']
2026-06-28T00:30:04Z Storing extracted assets at storage URI: s3://default-bucket/video
2026-06-28T00:31:16Z Batch processing finished. Success: 1, Failures: 0. Total accounted for: 1/1
2026-06-28T00:31:16Z Saved 0 extraction items for '/tmp-data/uploaded_files/video/Seattle Seahawks vs New England Patriots - Super Bowl LX Game Highlights.mp4'
2026-06-28T00:31:17Z == Batch 1 Ingestion completed in 72.72 seconds - Summary: Successfully processed 0 document(s) with 0 element(s) ==
2026-06-28T00:31:17Z ERROR:nvidia_rag.ingestor_server.main:NV-Ingest ingestion failed with no results.
2026-06-28T00:31:17Z WARNING:nvidia_rag.ingestor_server.main:Marked 1 files as FAILED for batch 1 due to ingestion failure
```

`compose-nv-ingest-ms-runtime-1` showed the real failure:

```text
2026-06-28T00:30:26Z ERROR parakeet.py:210 -- Error transcribing audio file:
DNS resolution failed for audio:50051: C-ares status is not ARES_SUCCESS qtype=AAAA name=audio is_balancer=0: Could not contact DNS servers
```

The same runtime failure then cascaded through the audio extractor:

```text
grpc._channel._InactiveRpcError:
  status = StatusCode.UNAVAILABLE
  details = "DNS resolution failed for audio:50051..."

ERROR audio_extraction.py:200 -- Error occurred while extracting audio data: 'str' object has no attribute 'condition'
WARNING nv_ingest_api.util.service_clients.redis.redis_client:Fragment 0 missing 'data' list or has wrong type. Skipping its data.
```

The result artifact was present but empty:

```text
/data/nv-ingest-results/video/Seattle_Seahawks_vs_New_England_Patriots_-_Super_Bowl_LX_Game_Highlights.mp4.results.jsonl.gz
line_count=0
```

The remote `docker ps` output did not include an `audio` service. It only showed `ingestor-server`, `compose-nv-ingest-ms-runtime-1`, `rag-server`, `rag-frontend`, and `ingestor-bridge` among the relevant services.

The NV-Ingest runtime container was configured to use the missing service:

```text
AUDIO_GRPC_ENDPOINT=audio:50051
AUDIO_INFER_PROTOCOL=grpc
```

## Why This Happened

The repo supports audio/video ingestion, but the ASR service is optional. MP4 files are treated as audio/video extraction inputs. During this run, NV-Ingest attempted to transcribe the MP4 audio through Riva ASR at `audio:50051`.

In `deploy/compose/nims.yaml`, the `audio` service is behind the `audio` profile:

```yaml
audio:
  image: ${AUDIO_IMAGE:-nvcr.io/nim/nvidia/riva-asr}:${AUDIO_TAG:-1.3.0}
  ports:
    - "8021:50051"
    - "8022:9000"
  profiles: ["audio"]
```

In `deploy/compose/docker-compose-ingestor-server.yaml`, NV-Ingest is wired to that service name:

```yaml
AUDIO_GRPC_ENDPOINT=audio:50051
AUDIO_INFER_PROTOCOL=grpc
```

Because the `audio` profile was not running, Docker DNS could not resolve `audio`, so the audio extractor failed. The client still reported the batch as accounted for, but the resulting JSONL contained no extraction records. The Python ingestor then raised `NV-Ingest ingestion failed with no results.` at `src/nvidia_rag/ingestor_server/main.py` when `results` was empty.

## Remote Fix Steps

Use this path if the Launchable VM is still running and has enough GPU capacity for Riva ASR.

1. Verify the current state:

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker ps --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}" | grep -i -E "audio|ingest|rag"'
```

2. Start the optional audio ASR service from the repo compose directory:

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'cd /home/shadeform/rag && USERID=$(id -u) docker compose -f deploy/compose/nims.yaml --profile audio up -d audio'
```

If the VM needs the NGC key supplied from the local shell, do not print it. Pipe it into SSH:

```bash
printf "%s\n" "$NGC_API_KEY" | ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'read -r NGC_API_KEY && export NGC_API_KEY && cd /home/shadeform/rag && USERID=$(id -u) docker compose -f deploy/compose/nims.yaml --profile audio up -d audio'
```

3. Wait for the audio container to become healthy:

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker ps --filter "name=audio" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"'
```

Expected container/service:

```text
compose-audio-1   Up ... (healthy)   0.0.0.0:8021->50051/tcp, 0.0.0.0:8022->9000/tcp
```

4. Confirm the NV-Ingest runtime can resolve the service name:

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker exec compose-nv-ingest-ms-runtime-1 getent hosts audio'
```

5. Retry the MP4 upload into a fresh collection or after deleting the failed document state. A fresh collection is cleaner for parity work:

```text
collection_name=video_retry
```

6. Watch the relevant logs:

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker logs -f --since 5m ingestor-server'
```

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker logs -f --since 5m compose-nv-ingest-ms-runtime-1'
```

A successful retry should no longer show `DNS resolution failed for audio:50051`, and the saved `.results.jsonl.gz` should contain one or more extraction records.

## Local Fix Steps

Use this path when reproducing on a local deployment from this checkout.

1. Make sure `NGC_API_KEY` is set in the shell or in the compose env file used by the deployment.

```bash
export NGC_API_KEY=...
```

2. Start the audio NIM with the `audio` profile:

```bash
USERID=$(id -u) docker compose -f deploy/compose/nims.yaml --profile audio up -d audio
```

3. Verify the service:

```bash
docker ps --filter "name=audio" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```

4. If running the Python ingestor/NV-Ingest through compose, start or restart the ingestor/NV-Ingest stack after the audio service is up so Docker DNS and service dependencies are stable:

```bash
docker compose -f deploy/compose/docker-compose-ingestor-server.yaml up -d
```

Use the same compose project/network as the NV-Ingest runtime. If `audio` is started under a different compose project/network, `audio:50051` will still not resolve from `compose-nv-ingest-ms-runtime-1`.

5. Confirm DNS from inside the NV-Ingest runtime:

```bash
docker exec compose-nv-ingest-ms-runtime-1 getent hosts audio
```

6. Retry the upload and verify the result file is not empty:

```bash
docker exec ingestor-server sh -lc \
  'gzip -cd /data/nv-ingest-results/video_retry/*.results.jsonl.gz | wc -l'
```

## If Audio ASR Cannot Be Started

If there is no spare GPU capacity or the Riva ASR image cannot be pulled, skip MP4/audio-video ingestion as a supported live baseline case for this instance. The observed failure is environmental: the required optional ASR dependency is absent.

For parity classification, record MP4 ingestion as:

```text
blocked_by_missing_optional_audio_asr_service
```

Do not classify it as a .NET parity gap unless the .NET path is expected to implement an alternative audio/video transcription backend without NV-Ingest/Riva ASR.

## Verification Commands Used

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker logs -t --since 48h ingestor-server 2>&1 | grep -F -C 12 "uploaded_files/video/Seattle Seahawks"'
```

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker logs -t --since 2026-06-28T00:29:45Z --until 2026-06-28T00:31:30Z compose-nv-ingest-ms-runtime-1 2>&1 | tail -n 260'
```

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker inspect compose-nv-ingest-ms-runtime-1 --format "{{range .Config.Env}}{{println .}}{{end}}" | grep -i -E "audio|asr|riva|parakeet|otel"'
```

```bash
ssh -i /tmp/rag-parity-codex-ssh shadeform@62.169.159.90 \
  'docker exec ingestor-server sh -lc "ls -l /data/nv-ingest-results/video 2>/dev/null; gzip -cd /data/nv-ingest-results/video/Seattle_Seahawks_vs_New_England_Patriots_-_Super_Bowl_LX_Game_Highlights.mp4.results.jsonl.gz 2>/dev/null | wc -l"'
```
