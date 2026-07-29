# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

SPEC 01 (scaffolding), SPEC 02 (document ingestion) and SPEC 03 (RAG pipeline and Knowledge
Orchestrator) are implemented. Both paths work end to end: `POST /api/ingest` reads
PDF/TXT/Markdown from a local path, chunks it, embeds it with Ollama and indexes it
idempotently into Qdrant; `POST /api/query` embeds the question, retrieves from Qdrant and
generates a grounded answer with Ollama, or reports `foundAnswer: false` without calling the
model.

Pending: the Confluence connector (own spec), the semantic cache (SPEC 04) and Postgres
persistence (SPEC 05). Until SPEC 05 there is **no relational store** — the only record of
what has been ingested is the Qdrant payload, and a query leaves no trace beyond its
OpenTelemetry spans. See "Ingestion pipeline", "Query pipeline" and "Build / test / run".

## What this project is

AI Knowledge Assistant: a corporate knowledge-query platform that answers natural-language
questions using internal documentation via RAG. The authoritative spec is `references/PRD.md`
(written in Spanish). Key MVP requirements distilled from it:

- REST API; no multi-turn chat, no autonomous agents (explicitly out of scope for MVP).
- Ingestion from Confluence, PDF, TXT, and Markdown → embeddings → Qdrant vector store.
- RAG pipeline that answers **only** from retrieved context (grounded answers, FR-003).
- Semantic cache (answer reuse by semantic similarity) — target <1s hits, >40% hit rate.
- Full RAG path target <5s.
- Persistence of questions/answers, user feedback, and complete audit trail.

## Intended architecture (from PRD §7)

```
User → API → Knowledge Orchestrator → Semantic Cache → RAG → Qdrant → LLM → Persistence → Response
```

The **Knowledge Orchestrator** is the central component: it checks the semantic cache
first, falls back to the RAG pipeline (retrieve from Qdrant → build grounded prompt → LLM),
then persists the Q/A and returns the response. Cache-before-RAG ordering is core to the
performance targets.

SPEC 03 built that component with the middle stage real and the outer two as declared seams:
the cache lookup always misses until SPEC 04, and persistence is a no-op until SPEC 05.

## Tech stack (from PRD §8)

- **C# / .NET Aspire** — orchestration host + service defaults + telemetry.
- **PostgreSQL** — Q/A persistence, feedback, audit.
- **Qdrant** — vector store for embeddings.
- **OpenTelemetry** (via Aspire) — observability is a hard NFR.
- **OpenAI / Ollama** — pluggable LLM + embedding providers.

## Conventions

Follow the vendored `dotnet-backend-patterns` skill (`.agents/skills/dotnet-backend-patterns/SKILL.md`)
for all C# code. It is the authoritative style guide for this repo. Highlights:

- Clean Architecture layering: `Domain` → `Application` → `Infrastructure` → `Api`.
- Async all the way; always thread `CancellationToken`; never `.Result`/`.Wait()`.
- `IOptions<T>` for typed config; constructor DI; keyed services for the pluggable
  LLM/embedding providers (OpenAI vs Ollama).
- Return `Result<T>` for business-flow outcomes instead of throwing.
- EF Core for the domain model; Dapper for read-heavy/perf-critical queries; `AsNoTracking()` on reads.
- Tests with xUnit + Moq (unit) and `WebApplicationFactory` (integration).

## Ingestion pipeline (SPEC 02)

```
POST /api/ingest → locate files → extract → hash → skip-if-unchanged → chunk → embed → upsert
```

Key types, by layer:

| Layer | Types |
| --- | --- |
| `Domain/Ingestion` | `RawDocument`, `DocumentChunk`, `SourceType`, `IngestionStatus`, `ContentHasher`, `ChunkIdFactory` |
| `Domain/Common` | `Result<T>` |
| `Application/Abstractions` | `IDocumentSource`, `IDocumentLocator`, `IEmbeddingGenerator`, `IVectorStore`, `ITextChunker` |
| `Application/Ingestion` | `IngestDocumentsHandler`, `FixedWindowTextChunker`, `IngestionSummary`, the four options classes, `IngestionTelemetry` |
| `Infrastructure/Ingestion` | `PdfDocumentSource` (PdfPig), `TextDocumentSource`, `MarkdownDocumentSource`, `FileSystemDocumentLocator`, `OllamaEmbeddingGenerator`, `QdrantVectorStore`, `QdrantPayload`, `VectorStoreInitializer` |

Behaviour that is easy to get wrong when changing this code:

- **Idempotency** rests on two things: SHA-256 of the extracted text (unchanged document →
  skipped, no embeddings regenerated) and the deterministic chunk GUID from
  `$"{sourceId}#{index}"` (re-ingesting overwrites points instead of duplicating them).
  `SourceId` is the absolute path, normalized to `/` and lowercased on Windows.
- **Best-effort**: individual failures land in `failed[]` and never abort the batch. A run
  returns `200` even with `ingested: 0`.
- **Bounded synchronously**: over `MaxFilesPerRequest` → `400 TooManyFiles` with nothing
  indexed; over `TimeoutSeconds` → the loop stops *between* documents (never mid-document),
  returns `200` with `status: "PartiallyCompleted"` and `remaining`. Repeating the same POST
  resumes, because what is already stored counts as skipped.
- **Extractors never throw** for expected problems: unreadable, corrupt or empty files come
  back as `Result.Failure` with codes like `PdfExtractionFailed`, `EmptyExtraction`.
- Token counts are an **approximation** (~4 chars ≈ 1 token), not a real tokenizer.
- `EnsureCollectionAsync` runs at startup via `VectorStoreInitializer`. A collection whose
  dimension differs from `Embeddings:Dimension` **fails the boot** on purpose; an unreachable
  Qdrant only logs an error, since the health checks already report it.
- Adding a format means implementing `IDocumentSource` and registering it in `AddIngestion`
  three ways: concrete type, keyed by discriminator, and in the `IDocumentSource` set the
  locator enumerates (keyed registrations are *not* part of that set).

## Query pipeline (SPEC 03)

```
POST /api/query → KnowledgeOrchestrator (cache seam → RAG → persistence seam)
                  RagPipeline: embed question → SearchAsync(TopK, MinScore) → grounded prompt → LLM
```

Key types, by layer:

| Layer | Types |
| --- | --- |
| `Domain/Rag` | `RetrievedChunk`, `Answer` |
| `Application/Abstractions` | `ILlmClient`, `LlmPrompt`, `LlmCompletion`, plus `SearchAsync` on `IVectorStore` |
| `Application/Rag` | `KnowledgeOrchestrator`, `RagPipeline`, `GroundedPromptBuilder`, `RagOptions`, `RagTelemetry` |
| `Infrastructure/Rag` | `OllamaLlmClient` (typed `HttpClient` against `/api/chat`) |
| `Infrastructure/DependencyInjection` | `RagRegistration.AddRag` |

Behaviour that is easy to get wrong when changing this code:

- **SPEC 03 widens a SPEC 02 contract**: `SearchAsync` is the one method added to
  `IVectorStore`. Unlike the rest of that interface it returns `Result<T>` rather than letting
  exceptions surface — Qdrant being down mid-query is an *expected* failure that must come out
  as `503 VectorSearchFailed`, not an opaque `500`. Both in-memory doubles (unit and
  integration) implement it, so changing the signature breaks the whole solution at once.
- **`KnowledgeOrchestrator` is the only entry point** of the use case; `QueryController` must
  never depend on `RagPipeline`. The cache (`TryGetCachedAnswerAsync`, SPEC 04) and persistence
  (`PersistAsync`, SPEC 05) seams are private methods with empty bodies, deliberately not TODOs:
  filling them is a change of body, not of shape.
- **No context means no generation.** When no chunk clears `Rag:MinScore` the pipeline returns
  `Answer(null, FoundAnswer: false, [], model, durationMs)` *without* calling `ILlmClient`. A
  unit test and an integration test both assert zero LLM calls; that assertion is the feature,
  not an incidental detail.
- `RetrievedChunk` is deliberately **not** `DocumentChunk`: the Qdrant payload stores no
  `TokenCount` and search contributes a `Score` that ingestion knows nothing about.
- **`includeChunks` is presentation only.** The pipeline always carries the chunk text in
  `Answer.Citations`; `CitationResponse` decides whether it is serialized, so there is one
  execution path behind both response shapes.
- `Answer.Model` comes from `ILlmClient.Model`, including on the no-results path.
- `OllamaLlmClient` owns its own deadline through a linked `CancellationTokenSource`
  (`CancelAfter(Rag:TimeoutSeconds)`), and `AddRag` **removes** the ServiceDefaults resilience
  handler for that client: its 10s attempt timeout would surface a slow generation as
  `LlmUnavailable` instead of the `LlmTimeout` the contract promises, and retrying a 60s
  generation just costs the user another minute.
- **Model choice is configuration** (`Rag:Model`): `llama3.2:3b` for development on CPU
  (10–20s per query), `qwen2.5:7b` as the target on a GPU host. The PRD's `<5s` target is
  explicitly **not** verifiable on CPU and is out of SPEC 03's acceptance criteria. Keep the
  value in step with the `chat` model resource tag in `AppHost.cs`.
- Failure codes the controller maps: `LlmTimeout` → `504`; `LlmUnavailable`,
  `VectorSearchFailed` and `EmbeddingRequestFailed` → `503`; blank question → `400`.

## Spec-driven workflow

Feature work goes through the `/spec-impl` workflow, configured in `specs/.spec-config.yml`.
`AutoCreateBranch: true` means `/spec-impl` auto-creates and checks out a `spec-NN-slug`
branch (no confirmation prompt).

## Skills

Vendored skills are pinned in `skills-lock.json` (source: `wshobson/agents`). The active one
is `dotnet-backend-patterns`. Update via the skills tooling rather than editing vendored files
by hand, so the lockfile hash stays consistent.

## Build / test / run

Targets **.NET 10** (`net10.0`, SDK 10.0.302) with **Aspire 13.4.6**; package versions are
centralized in `Directory.Packages.props` (Central Package Management — do not add inline
`Version=` to `.csproj` files). Ingestion adds `PdfPig` (the NuGet id of `UglyToad.PdfPig`),
`Qdrant.Client` and `Aspire.Qdrant.Client`.

- **Build:** `dotnet build AiKnowledgeAssistant.sln`
- **Test:** `dotnet test` — **no container runtime required**. The integration host replaces
  `IVectorStore`, `IEmbeddingGenerator` and `ILlmClient` with in-memory doubles and disables
  the Qdrant health check (`Aspire:Qdrant:Client:DisableHealthChecks`), so nothing dials out.
  Everything else — controller, orchestrator, pipeline, handler, locator, extractors, chunker —
  is the real thing, exercised against the sample corpus in
  `AiKnowledgeAssistant.UnitTests/Samples/` (linked into the integration project). Query tests
  populate the index through the real `POST /api/ingest` rather than seeding the double. Single
  test: `dotnet test --filter "FullyQualifiedName~<TestName>"`.
- **Run:** `dotnet run --project AiKnowledgeAssistant.AppHost` — starts the Aspire dashboard
  and the `api`, `postgres`, `qdrant`, `ollama`, `embedding` and `chat` resources. Requires a
  container runtime (Docker/Podman). The first run pulls `nomic-embed-text` (~270 MB) and
  `llama3.2:3b` (~2 GB), so both model resources stay `Starting` for a few minutes; the API
  does not wait for them, so ingest calls in that window report `EmbeddingRequestFailed` per
  document and queries return `503 LlmUnavailable`. Both are simply retried.

### Endpoints

| Endpoint | Behaviour |
| --- | --- |
| `POST /api/ingest` | `{ "path": "...", "sourceType": "markdown" }` — `sourceType` optional (`pdf`/`txt`/`markdown`); omitted means infer per file by extension, supplied means ingest only that format. `200` with the summary (`status`, `totalFiles`, `ingested`, `skipped`, `failedCount`, `remaining`, `chunksIndexed`, `durationMs`, `failed[]`). `400` for a missing path, unknown `sourceType`, no supported files, or `TooManyFiles`. |
| `GET /api/ingest/stats` | `200` with `{ collection, vectorsCount, dimension }`. |
| `POST /api/query` | `{ "question": "...", "includeChunks": false }` — `200` with `answer`, `foundAnswer`, `model`, `durationMs` and `citations[]` (`title`, `sourceId`, `sourceType`, `chunkIndex`, `score`, plus `text` only when `includeChunks` is true). Nothing above `Rag:MinScore` → `200` with `answer: null`, `foundAnswer: false`, `citations: []` and no LLM call. `400` for a blank or missing question; `503 { "error": "LlmUnavailable" \| "VectorSearchFailed" \| "EmbeddingRequestFailed" }`; `504 { "error": "LlmTimeout" }`. |
| `/health`, `/alive` | Aspire `MapDefaultEndpoints`, Development only. |

### Configuration

Defaults live in `AiKnowledgeAssistant.Api/appsettings.json`; the first four sections bind
through `IOptions<T>` in `AddIngestion`, the `Rag` section in `AddRag`.

| Section | Keys (defaults) |
| --- | --- |
| `Embeddings` | `Model` (`nomic-embed-text`), `Dimension` (`768`), `BatchSize` (`16`) |
| `Chunking` | `MaxTokens` (`500`), `OverlapTokens` (`50`) |
| `VectorStore` | `CollectionName` (`knowledge`), `Distance` (`Cosine`) |
| `Ingestion` | `MaxFilesPerRequest` (`100`), `TimeoutSeconds` (`90`) |
| `Rag` | `Model` (`llama3.2:3b`), `TopK` (`5`), `MinScore` (`0.5`), `TimeoutSeconds` (`60`), `MaxAnswerTokens` (`500`), `Temperature` (`0.2`) |

Three constraints worth remembering: keep `Ingestion:TimeoutSeconds` **below** the request
timeout of Kestrel and any proxy in front of it (typically 100–120s), or a slow batch dies on
the connection instead of returning its partial summary. Changing `Embeddings:Model` or
`Dimension` invalidates every indexed vector — the app refuses to boot against a collection of
a different dimension, so drop the collection and re-ingest. And `Rag:MinScore: 0.5` is an
**unvalidated** starting point: embedding models score unrelated texts higher than intuition
suggests, so tune it against the real corpus with `includeChunks: true`. `TopK` and `MinScore`
cannot be overridden per request, on purpose — SPEC 04's cache would otherwise have to version
its entries by parameter combination.

Traces and metrics come from two activity sources and meters: `AiKnowledgeAssistant.Ingestion`
(spans `ingest.batch` → `ingest.document` → `ingest.extract`/`chunk`/`embed`/`upsert`; counters
for documents ingested/skipped/failed, chunks indexed, and batch duration) and
`AiKnowledgeAssistant.Rag` (spans `query` → `query.embed`/`query.search`/`query.generate` with
`topK`, chunks retrieved, max score, model and error code; counters for queries answered,
without results and failed, plus a duration histogram tagged by stage). Each is registered with
OpenTelemetry inside its own `Add*` extension, not in `ServiceDefaults`.

Neither endpoint has **authentication** — assumed internal network, deferred to its own spec.
Do not deploy the API to an untrusted network before then: `path` reads arbitrary server-side
locations, and `POST /api/query` with `includeChunks: true` hands out the full text of indexed
document fragments.

Projects: `AiKnowledgeAssistant.{Domain,Application,Infrastructure,Api,AppHost,ServiceDefaults}`
plus `AiKnowledgeAssistant.{UnitTests,IntegrationTests}`. Clean Architecture layering:
`Domain` (no deps) → `Application` → `Infrastructure`; `Api` references `Application`,
`Infrastructure` and `ServiceDefaults`. API endpoints are **MVC controllers**
(`Controllers/*Controller.cs`), not minimal API. Health endpoints (`/health`, `/alive`)
come from Aspire's `MapDefaultEndpoints` and are only mapped in the Development environment.
