# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

Greenfield / pre-implementation. No source code, build system, or git repo exists yet.
The repository currently holds a product spec, a spec-driven workflow config, and a
vendored .NET patterns skill. The first implementation work will scaffold the .NET
Aspire solution described below.

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

## Spec-driven workflow

Feature work goes through the `/spec-impl` workflow, configured in `specs/.spec-config.yml`.
`AutoCreateBranch: true` means `/spec-impl` auto-creates and checks out a `spec-NN-slug`
branch (no confirmation prompt). Note: git is not yet initialized — initialize it before
the first spec branch.

## Skills

Vendored skills are pinned in `skills-lock.json` (source: `wshobson/agents`). The active one
is `dotnet-backend-patterns`. Update via the skills tooling rather than editing vendored files
by hand, so the lockfile hash stays consistent.

## Build / test / run

No solution exists yet, so there are no project-specific commands. Once the .NET Aspire
solution is scaffolded, the standard commands will be `dotnet build`, `dotnet test`
(single test: `dotnet test --filter "FullyQualifiedName~<TestName>"`), and running the
Aspire AppHost with `dotnet run` from the AppHost project. Update this section once the
solution structure is in place.
