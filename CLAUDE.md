# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

SPEC 01 (scaffolding) and SPEC 02 (document ingestion) are implemented. The ingestion
pipeline works end to end: `POST /api/ingest` reads PDF/TXT/Markdown from a local path,
chunks it, embeds it with Ollama and indexes it idempotently into Qdrant. `POST /api/query`
is still a 501 stub.

Pending: the Confluence connector (own spec), the RAG pipeline / Knowledge Orchestrator
(SPEC 03), the semantic cache (SPEC 04) and Postgres persistence (SPEC 05). Until SPEC 05
there is **no relational store** — the only record of what has been ingested is the Qdrant
payload. See "Ingestion pipeline" and "Build / test / run".

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
  `IVectorStore` and `IEmbeddingGenerator` with in-memory doubles and disables the Qdrant
  health check (`Aspire:Qdrant:Client:DisableHealthChecks`), so nothing dials out. Everything
  else — controller, handler, locator, extractors, chunker — is the real thing, exercised
  against the sample corpus in `AiKnowledgeAssistant.UnitTests/Samples/` (linked into the
  integration project). Single test: `dotnet test --filter "FullyQualifiedName~<TestName>"`.
- **Run:** `dotnet run --project AiKnowledgeAssistant.AppHost` — starts the Aspire
  dashboard and the `api`, `postgres`, `qdrant`, `ollama` and `embedding` resources. Requires
  a container runtime (Docker/Podman). The `embedding` resource pulls `nomic-embed-text`
  (~270 MB) on the first run and stays `Starting` for a few minutes; the API does not wait for
  it, so ingest calls made in that window report `EmbeddingRequestFailed` per document and can
  simply be retried.

### Endpoints

| Endpoint | Behaviour |
| --- | --- |
| `POST /api/ingest` | `{ "path": "...", "sourceType": "markdown" }` — `sourceType` optional (`pdf`/`txt`/`markdown`); omitted means infer per file by extension, supplied means ingest only that format. `200` with the summary (`status`, `totalFiles`, `ingested`, `skipped`, `failedCount`, `remaining`, `chunksIndexed`, `durationMs`, `failed[]`). `400` for a missing path, unknown `sourceType`, no supported files, or `TooManyFiles`. |
| `GET /api/ingest/stats` | `200` with `{ collection, vectorsCount, dimension }`. |
| `POST /api/query` | `501` — SPEC 03. |
| `/health`, `/alive` | Aspire `MapDefaultEndpoints`, Development only. |

### Configuration

Defaults live in `AiKnowledgeAssistant.Api/appsettings.json`; all four sections bind through
`IOptions<T>` in `AddIngestion`.

| Section | Keys (defaults) |
| --- | --- |
| `Embeddings` | `Model` (`nomic-embed-text`), `Dimension` (`768`), `BatchSize` (`16`) |
| `Chunking` | `MaxTokens` (`500`), `OverlapTokens` (`50`) |
| `VectorStore` | `CollectionName` (`knowledge`), `Distance` (`Cosine`) |
| `Ingestion` | `MaxFilesPerRequest` (`100`), `TimeoutSeconds` (`90`) |

Two constraints worth remembering: keep `Ingestion:TimeoutSeconds` **below** the request
timeout of Kestrel and any proxy in front of it (typically 100–120s), or a slow batch dies on
the connection instead of returning its partial summary. And changing `Embeddings:Model` or
`Dimension` invalidates every indexed vector — the app refuses to boot against a collection of
a different dimension, so drop the collection and re-ingest.

Traces and metrics come from the `AiKnowledgeAssistant.Ingestion` activity source and meter
(spans `ingest.batch` → `ingest.document` → `ingest.extract`/`chunk`/`embed`/`upsert`; counters
for documents ingested/skipped/failed, chunks indexed, and batch duration).

The ingest endpoint has **no authentication** — assumed internal network, deferred to its own
spec. Do not deploy the API to an untrusted network before then: `path` reads arbitrary
server-side locations.

Projects: `AiKnowledgeAssistant.{Domain,Application,Infrastructure,Api,AppHost,ServiceDefaults}`
plus `AiKnowledgeAssistant.{UnitTests,IntegrationTests}`. Clean Architecture layering:
`Domain` (no deps) → `Application` → `Infrastructure`; `Api` references `Application`,
`Infrastructure` and `ServiceDefaults`. API endpoints are **MVC controllers**
(`Controllers/*Controller.cs`), not minimal API. Health endpoints (`/health`, `/alive`)
come from Aspire's `MapDefaultEndpoints` and are only mapped in the Development environment.
