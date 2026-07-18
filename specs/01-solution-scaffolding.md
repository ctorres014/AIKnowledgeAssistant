# SPEC 01 — Andamiaje de la solución .NET Aspire

> **Status:** aprobado
> **Depends on:** — (ninguno)
> **Date:** 2026-07-18
> **Objective:** Crear el andamiaje ejecutable de la solución .NET Aspire con arquitectura limpia (Domain/Application/Infrastructure/Api), recursos Postgres/Qdrant/Ollama cableados y telemetría, sin lógica de negocio.

## Por qué este spec existe

La plataforma completa del PRD no cabe en un spec. Sin una base ejecutable y con las capas de Clean
Architecture ya delimitadas, los specs de ingesta y RAG improvisarían estructura y dependencias. Este
spec fija el esqueleto (proyectos, referencias de capa, recursos de infra, telemetría) para que el
trabajo posterior solo añada lógica, nunca andamiaje.

## Scope

**In:**

- `git init` + `.gitignore` estándar de .NET.
- Solución `AiKnowledgeAssistant.sln` con proyectos: `AiKnowledgeAssistant.Domain`, `.Application`, `.Infrastructure`, `.Api`, `.AppHost`, `.ServiceDefaults`.
- Dos proyectos de test: `AiKnowledgeAssistant.UnitTests` (xUnit + Moq) y `AiKnowledgeAssistant.IntegrationTests` (`WebApplicationFactory`).
- `AppHost` cablea como recursos Aspire: PostgreSQL (con una base de datos), Qdrant y Ollama.
- `ServiceDefaults` con OpenTelemetry, health checks, service discovery y resiliencia HTTP.
- `Api` referencia `ServiceDefaults`, `Application` e `Infrastructure`; expone endpoints de salud y `POST /api/query` que devuelve `501 Not Implemented`.
- Referencias de proyecto según capas: `Domain` sin dependencias; `Application → Domain`; `Infrastructure → Application`; `Api → Application + Infrastructure`; `AppHost → Api`.
- Central Package Management (`Directory.Packages.props`) para fijar versiones.

**Out of scope (para specs futuros):**

- Pipeline de ingesta y conectores (Confluence/PDF/TXT/MD) → SPEC 02.
- Pipeline RAG y Knowledge Orchestrator → SPEC 03.
- Cache semántico → SPEC 04.
- Persistencia real de Q/A, feedback, auditoría y migraciones EF Core → SPEC 05.
- Autenticación / identidad corporativa → spec propio.
- Lógica de embeddings o llamadas al LLM: en SPEC 01 solo se **cablea** el recurso Ollama, no se invoca.
- OpenAI como proveedor: queda pluggable para más adelante; el primer hito es solo Ollama.

## Data model

Este spec **no introduce estructuras de dominio ni tablas**. No hay entidades EF Core, no hay migraciones,
no hay esquemas de Qdrant. El único "modelo" es la topología de recursos declarada en el `AppHost` y la
configuración tipada mínima (`appsettings.json`) que Aspire inyecta por convención (cadenas de conexión de
Postgres/Qdrant y endpoint de Ollama vía `WithReference`). Las entidades de dominio (Q/A, Feedback, Audit)
llegan en SPEC 05.

Contrato placeholder del endpoint (forma, no lógica):

```
POST /api/query
Request:  { "question": "string" }
Response: 501 Not Implemented   // la lógica llega en SPEC 03
```

## Implementation plan

1. `git init`, añadir `.gitignore` de .NET y crear `AiKnowledgeAssistant.sln` vacía. Commit.
2. Crear `AiKnowledgeAssistant.ServiceDefaults` (`dotnet new aspire-servicedefaults`). Verificación: compila.
3. Crear `AiKnowledgeAssistant.AppHost` (`dotnet new aspire-apphost`). Verificación: `dotnet run` levanta el dashboard vacío de Aspire.
4. Crear `Domain`, `Application`, `Infrastructure` como `classlib` con las referencias de capa (Domain sin deps; Application→Domain; Infrastructure→Application). Verificación: compila.
5. Crear `AiKnowledgeAssistant.Api` (Web API minimal), referenciar `ServiceDefaults` + `Application` + `Infrastructure`; llamar `AddServiceDefaults()` y `MapDefaultEndpoints()` (health `/health`, `/alive`). Verificación: `GET /health` → 200.
6. En `AppHost`: `AddProject<Api>()`; añadir `AddPostgres(...).AddDatabase(...)`, `AddQdrant(...)` y `AddOllama(...)`; pasar `WithReference(...)` de los tres al proyecto `Api`. Verificación: dashboard muestra api + postgres + qdrant + ollama en `Running`.
7. En `Api`: mapear `POST /api/query` que devuelve `Results.StatusCode(501)`. Verificación: `POST /api/query` → 501.
8. Añadir `Directory.Packages.props` (Central Package Management) y mover las versiones de paquete allí. Verificación: `dotnet build` sin warnings de versión.
9. Crear `UnitTests` (xUnit + Moq) con un test de humo, e `IntegrationTests` (`WebApplicationFactory`) con dos tests: `GET /health` → 200 y `POST /api/query` → 501. Verificación: `dotnet test` pasa.
10. Actualizar la sección "Build / test / run" de `CLAUDE.md` con los comandos reales (build, test, `dotnet run` desde AppHost). Verificación: comandos documentados coinciden con la solución.

## Acceptance criteria

- [ ] `dotnet build` compila la solución completa sin errores.
- [ ] `dotnet run` desde el proyecto AppHost levanta el dashboard de Aspire.
- [ ] El dashboard muestra `api`, `postgres`, `qdrant` y `ollama` en estado `Running`.
- [ ] `GET /health` y `GET /alive` en `Api` devuelven `200`.
- [ ] `POST /api/query` devuelve `501 Not Implemented`.
- [ ] `Domain` no referencia ningún otro proyecto de la solución.
- [ ] Las referencias de capa respetan Clean Architecture (Api no es referenciado por Application ni Domain).
- [ ] `dotnet test` pasa: test de humo unitario + 2 tests de integración (health 200, query 501).
- [ ] Las trazas de OpenTelemetry son visibles en el dashboard de Aspire.
- [ ] Existe `Directory.Packages.props` y ningún `.csproj` fija versiones de paquete inline.

## Decisions

- **Sí:** namespace `AiKnowledgeAssistant` (sigue el nombre de la carpeta). **No:** `KnowledgeAssistant` / prefijo de compañía — descartados por preferencia del usuario.
- **Sí:** cablear Postgres + Qdrant + Ollama ya, aunque ningún código los use. Base de infra completa desde el día 1. **No:** diferir Ollama — descartado porque el objetivo del primer hito es Ollama local.
- **Sí:** stub `POST /api/query` → 501 para fijar la forma del contrato. **No:** solo health / `GET /ping` — descartados por no insinuar el contrato real de consulta.
- **Sí:** .NET 10 + Aspire actual (revisado durante la implementación: el único SDK instalado es 10.0.301, así que se apunta a `net10.0` en lugar del `net9.0` originalmente previsto). **No:** .NET 8 LTS — descartado por ser greenfield sin restricción corporativa declarada.
- **Sí:** Ollama como único proveedor objetivo del primer hito; la abstracción pluggable (keyed services OpenAI vs Ollama) se materializa en SPEC 03. **No:** cablear OpenAI ahora.
- **Sí:** autenticación diferida a su propio spec; MVP funcional sin auth primero.
- **Sí:** Central Package Management para versionado consistente con el skill `dotnet-backend-patterns`.
- **Sí:** la config del workflow vive en `specs/.spec-config.yml` (con `AutoCreateBranch: true`), ya alineada con la ruta de los specs.

## Risks

| Riesgo | Mitigación |
| --- | --- |
| El contenedor de Ollama es pesado y descargar modelos ralentiza el arranque | SPEC 01 solo cablea el recurso, **no** descarga ni invoca modelos; la descarga se gestiona en SPEC 03. |
| Versiones de Aspire/paquetes cambiantes rompen el build | Fijar versiones en `Directory.Packages.props` (Central Package Management). |

## What is **not** in this spec

- Ingesta y conectores (Confluence/PDF/TXT/MD) → SPEC 02.
- Pipeline RAG y Knowledge Orchestrator → SPEC 03.
- Cache semántico → SPEC 04.
- Persistencia de Q/A, feedback y auditoría → SPEC 05.
- Autenticación / identidad corporativa → spec propio.
- Cualquier invocación real a Ollama u OpenAI.

Cada uno de estos, cuando llegue, va en su propio spec.
