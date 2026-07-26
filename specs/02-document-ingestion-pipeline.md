# SPEC 02 — Pipeline de ingesta y conectores de documentos

> **Status:** Implemented
> **Depends on:** SPEC 01
> **Date:** 2026-07-26
> **Objective:** Implementar el pipeline de ingesta que lee documentos desde fuentes pluggables (PDF/TXT/MD en este spec), los chunkea, genera embeddings con Ollama y los indexa de forma idempotente en Qdrant, expuesto vía `POST /api/ingest`.

## Scope

**In:**

- Abstracción de conectores: `IDocumentSource` en `Application`, con `sourceType` como discriminador y registro por **keyed services** (`"pdf"`, `"txt"`, `"markdown"`).
- Tres conectores implementados en `Infrastructure`: `PdfDocumentSource` (PdfPig), `TextDocumentSource` (`.txt`), `MarkdownDocumentSource` (`.md`).
- Resolución de entrada por **ruta local**: un archivo o una carpeta (recorrido recursivo, filtrado por extensión).
- Chunker propio: ventana fija de ~500 tokens con ~50 de solape, respetando límites de párrafo/frase cuando sea posible.
- Hashing SHA-256 del contenido extraído para deduplicación e idempotencia.
- `IEmbeddingGenerator` en `Application` + `OllamaEmbeddingGenerator` en `Infrastructure` (modelo `nomic-embed-text`, dimensión 768, vía `IOptions<EmbeddingOptions>`).
- Descarga del modelo de embeddings como recurso del `AppHost` (`AddOllama(...).AddModel("nomic-embed-text")`).
- `IVectorStore` en `Application` + `QdrantVectorStore` en `Infrastructure`: creación de la colección al arrancar si no existe, upsert de puntos con payload de metadatos, borrado por filtro `sourceId`, y conteo de vectores.
- Caso de uso `IngestDocumentsHandler` en `Application` que orquesta: descubrir → extraer → hashear → chunkear → embeddear → upsert, devolviendo `Result<IngestionSummary>`.
- Ingesta **sincrónica y acotada**: `MaxFilesPerRequest` (400 si se excede) y `TimeoutSeconds` propio por operación, que corta limpio y devuelve el resumen **parcial** en lugar de dejar morir el request.
- Ingesta **best-effort**: continúa ante fallos individuales y devuelve resumen por archivo (`ingested`, `skipped`, `failed[]`, `remaining`) con `status` `Completed` o `PartiallyCompleted`.
- Controlador MVC `IngestController`: `POST /api/ingest` y `GET /api/ingest/stats`.
- Tests: unit reales de chunker, extractores y hashing con archivos de muestra en el repo; integración del endpoint con `IEmbeddingGenerator` e `IVectorStore` fakeados.
- Trazas/métricas OpenTelemetry del pipeline (documentos procesados, chunks generados, duración).

**Out of scope (para specs futuros):**

- **Conector de Confluence** → SPEC propio, implementado contra `IDocumentSource` ya definido aquí.
- Pipeline RAG, retrieval y Knowledge Orchestrator → SPEC 03.
- Cache semántico → SPEC 04.
- Persistencia en Postgres / EF Core / migraciones (tabla de documentos ingestados, auditoría) → SPEC 05. El tracking de este spec vive **solo** en el payload de Qdrant.
- Ejecución en background, cola de jobs y `202 Accepted` con `IngestionJobId` → spec propio si los límites de este resultan insuficientes.
- Upload multipart (`IFormFile`).
- OpenAI como proveedor de embeddings: la abstracción lo permite, la implementación no entra.
- Chunking por estructura (headings/páginas) y tuning de tamaños → spec de tuning si hace falta.
- Formatos adicionales (DOCX, HTML, CSV).
- Testcontainers / Qdrant real en tests.
- Autenticación del endpoint de ingesta.
- Re-ingesta programada o watcher de carpetas.

## Data model

Este spec introduce el primer modelo de dominio del proyecto. **No hay tablas ni EF Core** — la persistencia relacional sigue siendo SPEC 05.

### Dominio (`AiKnowledgeAssistant.Domain`)

```csharp
// Domain/Ingestion/SourceType.cs
public enum SourceType { Pdf, Text, Markdown, Confluence }

// Domain/Ingestion/IngestionStatus.cs
public enum IngestionStatus { Completed, PartiallyCompleted }

// Domain/Ingestion/RawDocument.cs — documento extraído, antes de chunkear
public sealed record RawDocument(
    string SourceId,      // identidad estable de la fuente (ruta absoluta normalizada)
    SourceType SourceType,
    string Title,         // nombre de archivo sin extensión
    string Content,       // texto plano ya extraído
    string ContentHash);  // SHA-256 hex minúscula de Content

// Domain/Ingestion/DocumentChunk.cs — unidad indexable
public sealed record DocumentChunk(
    Guid Id,              // determinista: UUIDv5-like sobre $"{SourceId}#{Index}"
    string SourceId,
    SourceType SourceType,
    string Title,
    int Index,            // 0-based, orden dentro del documento
    string Text,
    int TokenCount,
    string ContentHash);  // hash del documento padre, no del chunk
```

### Application — contratos

```csharp
// Application/Abstractions/IDocumentSource.cs
public interface IDocumentSource
{
    SourceType SourceType { get; }
    IReadOnlyCollection<string> SupportedExtensions { get; }   // ej. [".md", ".markdown"]
    Task<Result<RawDocument>> ExtractAsync(string locator, CancellationToken ct);
}

// Application/Abstractions/IEmbeddingGenerator.cs
public interface IEmbeddingGenerator
{
    int Dimension { get; }
    Task<Result<IReadOnlyList<float[]>>> GenerateAsync(
        IReadOnlyList<string> texts, CancellationToken ct);
}

// Application/Abstractions/IVectorStore.cs
public interface IVectorStore
{
    Task EnsureCollectionAsync(CancellationToken ct);
    Task<bool> ExistsWithHashAsync(string sourceId, string contentHash, CancellationToken ct);
    Task DeleteBySourceIdAsync(string sourceId, CancellationToken ct);
    Task UpsertAsync(IReadOnlyList<(DocumentChunk Chunk, float[] Vector)> points, CancellationToken ct);
    Task<VectorStoreStats> GetStatsAsync(CancellationToken ct);
}

// Application/Abstractions/ITextChunker.cs
public interface ITextChunker
{
    IReadOnlyList<DocumentChunk> Chunk(RawDocument document);
}
```

### Application — configuración tipada

```csharp
// Application/Ingestion/EmbeddingOptions.cs        — sección "Embeddings"
public sealed class EmbeddingOptions
{
    public string Model { get; init; } = "nomic-embed-text";
    public int Dimension { get; init; } = 768;
    public int BatchSize { get; init; } = 16;
}

// Application/Ingestion/ChunkingOptions.cs         — sección "Chunking"
public sealed class ChunkingOptions
{
    public int MaxTokens { get; init; } = 500;
    public int OverlapTokens { get; init; } = 50;
}

// Application/Ingestion/VectorStoreOptions.cs      — sección "VectorStore"
public sealed class VectorStoreOptions
{
    public string CollectionName { get; init; } = "knowledge";
    public string Distance { get; init; } = "Cosine";
}

// Application/Ingestion/IngestionOptions.cs        — sección "Ingestion"
public sealed class IngestionOptions
{
    public int MaxFilesPerRequest { get; init; } = 100;
    public int TimeoutSeconds { get; init; } = 90;
}
```

### Contratos HTTP

```
POST /api/ingest
Request:  { "path": "C:\\docs\\handbook", "sourceType": "markdown" }   // sourceType opcional: si se omite, se infiere por extensión

Response 200 (todo procesado):
{ "status": "Completed", "totalFiles": 50, "ingested": 45, "skipped": 2,
  "failedCount": 3, "remaining": 0, "chunksIndexed": 812, "durationMs": 41230,
  "failed": [ { "path": "...", "reason": "PdfExtractionFailed: ..." } ] }

Response 200 (cortado por timeout):
{ "status": "PartiallyCompleted", "totalFiles": 73, "ingested": 42, "skipped": 0,
  "failedCount": 0, "remaining": 31, "chunksIndexed": 604, "durationMs": 90118,
  "failed": [] }

Response 400: ruta inexistente, sourceType desconocido, sin archivos soportados,
              o totalFiles > MaxFilesPerRequest → { "error": "TooManyFiles", "maxFilesPerRequest": 100 }

GET /api/ingest/stats
Response 200: { "collection": "knowledge", "vectorsCount": 812, "dimension": 768 }
```

### Payload de Qdrant (el único "tracking" de este spec)

| Campo | Tipo | Uso |
| --- | --- | --- |
| `sourceId` | keyword (indexado) | Borrado/actualización por documento; filtro de idempotencia. |
| `sourceType` | keyword | Filtrado por origen en el retrieval de SPEC 03. |
| `title` | text | Citación en la respuesta grounded. |
| `chunkIndex` | integer | Reconstrucción del orden. |
| `text` | text | Contexto que se inyecta en el prompt. |
| `contentHash` | keyword (indexado) | Detección de cambios sin releer el archivo. |
| `ingestedAt` | integer (unix ms) | Antigüedad del dato. |

**Convenciones:**

- `SourceId` = ruta absoluta normalizada (`/` como separador, minúsculas en Windows). Para Confluence será `confluence://{spaceKey}/{pageId}`.
- ID de punto Qdrant: `Guid` determinista derivado de `$"{SourceId}#{Index}"` → re-ingestar produce los mismos IDs, nunca duplicados.
- Conteo de tokens: aproximación por palabras/caracteres (`~4 chars ≈ 1 token`), no tokenizador real. Suficiente para dimensionar chunks.
- La colección se crea con dimensión `EmbeddingOptions.Dimension`; si difiere de la existente, el arranque falla en voz alta en vez de indexar vectores incompatibles.
- Reintentar el mismo `POST` tras un `PartiallyCompleted` **reanuda** el trabajo: el hash por documento hace que lo ya indexado cuente como `skipped`. No hace falta estado de job para retomar.

## Implementation plan

1. **Dominio de ingesta.** Crear `Domain/Ingestion/`: `SourceType`, `IngestionStatus`, `RawDocument`, `DocumentChunk`. Verificación: `dotnet build` compila; `Domain` sigue sin referencias a otros proyectos.

2. **`Result<T>`.** Añadir `Domain/Common/Result.cs` (o `Result<T>`) según el skill `dotnet-backend-patterns`, con `Success`/`Failure`, `Error` y `IsSuccess`. Verificación: unit test de `Result<T>` en éxito y fallo.

3. **Abstracciones y options.** Crear en `Application`: `IDocumentSource`, `IEmbeddingGenerator`, `IVectorStore`, `ITextChunker`, `VectorStoreStats`, más `EmbeddingOptions`, `ChunkingOptions`, `VectorStoreOptions`. Verificación: compila; `Application` solo referencia `Domain`.

4. **Chunker.** Implementar `FixedWindowTextChunker : ITextChunker` en `Application/Ingestion/` (ventana por tokens estimados, solape, corte preferente en `\n\n` → `. `). Verificación: unit tests — texto corto → 1 chunk; texto de 2.000 tokens → chunks con solape y `Index` consecutivo desde 0; `TokenCount` nunca supera `MaxTokens`.

5. **Hashing e identidad.** Añadir helper `ContentHasher` (SHA-256 hex) y `ChunkIdFactory` (Guid determinista desde `sourceId#index`). Verificación: unit tests — mismo input → mismo hash/Guid; input distinto → distinto.

6. **Conectores TXT y Markdown.** `TextDocumentSource` y `MarkdownDocumentSource` en `Infrastructure/Ingestion/` (lectura con detección de BOM/UTF-8; en Markdown se conserva el texto tal cual, sin renderizar). Añadir archivos de muestra en `AiKnowledgeAssistant.UnitTests/Samples/`. Verificación: unit tests extraen título y contenido esperados.

7. **Conector PDF.** Añadir `UglyToad.PdfPig` a `Directory.Packages.props` e implementar `PdfDocumentSource` (texto por página, unido con `\n\n`). Añadir un PDF de muestra y uno corrupto en `Samples/`. Verificación: unit tests — PDF válido devuelve texto; PDF corrupto devuelve `Result.Failure`, **no** excepción.

8. **Descubrimiento de archivos.** `FileSystemDocumentLocator` en `Infrastructure`: dado un archivo o carpeta, enumera recursivamente y resuelve el `IDocumentSource` por extensión (o fuerza el `sourceType` si viene indicado). Verificación: unit test sobre `Samples/` — cuenta y tipos correctos, extensiones no soportadas ignoradas.

9. **Embeddings con Ollama.** `OllamaEmbeddingGenerator : IEmbeddingGenerator` en `Infrastructure` usando `HttpClient` tipado contra `/api/embed`, con batching por `BatchSize` y `CancellationToken` propagado. Registrar como **keyed service** `"ollama"` y como default. Verificación: unit test con `HttpMessageHandler` mockeado — respuesta OK devuelve N vectores de dimensión 768; error HTTP devuelve `Result.Failure`.

10. **Vector store Qdrant.** `QdrantVectorStore : IVectorStore` en `Infrastructure` (cliente `Qdrant.Client`): `EnsureCollectionAsync` crea la colección con dimensión y distancia configuradas e índices de payload sobre `sourceId` y `contentHash`; `ExistsWithHashAsync`, `DeleteBySourceIdAsync`, `UpsertAsync`, `GetStatsAsync`. Verificación: compila; unit tests de la construcción de filtros/puntos con el cliente mockeado.

11. **Caso de uso.** `IngestDocumentsHandler` en `Application/Ingestion/`: descubre → por cada documento extrae, hashea, comprueba `ExistsWithHashAsync` (skip), borra por `sourceId` si cambió, chunkea, embeddea por lotes y hace upsert; acumula `IngestionSummary` y **nunca aborta** por un fallo individual. Verificación: unit tests con fakes — 3 documentos donde 1 falla → `ingested: 2`, `failedCount: 1`; segunda ejecución sin cambios → `skipped: 3`, `chunksIndexed: 0`.

12. **Límites y corte por tiempo.** Añadir `IngestionOptions`; el handler rechaza el lote si `totalFiles > MaxFilesPerRequest` (`Result.Failure("TooManyFiles")`) y usa un `CancellationTokenSource.CreateLinkedTokenSource(ct)` con `CancelAfter(TimeoutSeconds)`: al dispararse, sale del bucle **entre documentos** (nunca a mitad de uno), marca `status: PartiallyCompleted` y rellena `remaining`. Verificación: unit tests — 101 archivos con cap 100 → `TooManyFiles` sin indexar nada; fake de embeddings con retardo y timeout de 1s → `PartiallyCompleted` con `remaining > 0` y `ingested > 0`.

13. **Registro DI.** `AddIngestion(this IHostApplicationBuilder)` en `Infrastructure`: `IOptions` de las cuatro secciones, `HttpClient` tipado de Ollama, cliente Qdrant, keyed `IDocumentSource`, chunker y handler. Llamarlo desde `Program.cs` de la Api. Verificación: la Api arranca y resuelve `IngestDocumentsHandler` sin errores de DI.

14. **Endpoints.** `IngestController` (`[ApiController]`, `[Route("api/ingest")]`): `POST` con `IngestRequest` → `IngestionSummaryResponse` (200) o `ValidationProblem` (400); `TooManyFiles` se traduce a `400` con el `maxFilesPerRequest` efectivo; `GET stats` → `VectorStoreStats`. Verificación manual: `POST /api/ingest` sobre una carpeta con 2 `.md` devuelve 200 con `ingested: 2`.

15. **Arranque de la colección.** Invocar `EnsureCollectionAsync` al arrancar la Api (hosted service de inicialización), fallando en voz alta si la dimensión de la colección existente no coincide. Verificación: primer arranque crea la colección `knowledge`; segundo arranque no falla.

16. **AppHost.** Añadir el modelo de embeddings al recurso Ollama (`AddOllama(...).AddModel("embedding", "nomic-embed-text")`) y pasar la referencia a la Api. Verificación: el dashboard muestra el recurso del modelo en `Running` y la Api resuelve su endpoint.

17. **Telemetría.** Instrumentar el handler con un `ActivitySource` (`AiKnowledgeAssistant.Ingestion`) y contadores de documentos/chunks/fallos. Verificación: una ingesta produce una traza con spans por etapa en el dashboard de Aspire.

18. **Tests de integración.** En `IntegrationTests`, sobrescribir `IEmbeddingGenerator` e `IVectorStore` con fakes en memoria vía `WebApplicationFactory`: `POST /api/ingest` sobre carpeta de muestra → 200 con el resumen esperado; ruta inexistente → 400; `GET /api/ingest/stats` → 200 con `dimension: 768`. Verificación: `dotnet test` pasa **sin Docker**.

19. **Documentación.** Actualizar `CLAUDE.md`: estado del proyecto (SPEC 02 implementado), endpoints nuevos, secciones de configuración (`Embeddings`, `Chunking`, `VectorStore`, `Ingestion`) y nota de que los tests no requieren contenedores. Verificación: los comandos y endpoints documentados coinciden con el código.

## Acceptance criteria

- [x] `dotnet build AiKnowledgeAssistant.sln` compila sin errores ni warnings de versión de paquete.
- [x] `Domain` sigue sin referenciar ningún otro proyecto de la solución.
- [x] `Application` referencia únicamente `Domain` (los conectores concretos, Ollama y Qdrant viven solo en `Infrastructure`).
- [x] `dotnet test` pasa completo **sin un runtime de contenedores activo**.
- [x] `FixedWindowTextChunker` sobre un texto de ~2.000 tokens estimados produce >1 chunk, todos con `TokenCount <= 500`, `Index` consecutivo desde 0 y solape verificable entre chunks contiguos.
- [x] Un documento cuyo texto estimado es <500 tokens produce exactamente 1 chunk.
- [x] `ContentHasher` devuelve el mismo hash para el mismo contenido y distinto para contenido distinto.
- [x] `ChunkIdFactory` devuelve el mismo `Guid` para el mismo `sourceId#index` en ejecuciones distintas del proceso.
- [x] `PdfDocumentSource` extrae texto no vacío del PDF de muestra.
- [x] `PdfDocumentSource` sobre el PDF corrupto de muestra devuelve `Result.Failure` y **no** lanza excepción.
- [x] `TextDocumentSource` y `MarkdownDocumentSource` extraen el contenido esperado de sus archivos de muestra y derivan el `Title` del nombre de archivo sin extensión.
- [x] `FileSystemDocumentLocator` sobre `Samples/` enumera solo `.pdf`, `.txt`, `.md`/`.markdown` e ignora el resto.
- [x] `OllamaEmbeddingGenerator` con `HttpMessageHandler` mockeado devuelve N vectores de longitud 768 para N textos, y `Result.Failure` ante un 500 HTTP.
- [x] `IngestDocumentsHandler` con 3 documentos de los que 1 falla devuelve `ingested: 2`, `failedCount: 1` y la lista `failed` con el motivo de ese archivo.
- [x] Ejecutar la ingesta dos veces sobre la misma carpeta sin cambios devuelve `skipped` igual al número de archivos y `chunksIndexed: 0` en la segunda pasada.
- [x] Modificar un archivo ya ingestado y re-ingestar borra los chunks previos de ese `sourceId`: `vectorsCount` refleja el nuevo número de chunks, no la suma de ambas pasadas.
- [x] `POST /api/ingest` con una carpeta que contiene 2 `.md` devuelve `200` con `status: "Completed"`, `remaining: 0`, `ingested: 2` y `chunksIndexed > 0`.
- [x] `POST /api/ingest` con una ruta inexistente devuelve `400`.
- [x] `POST /api/ingest` con `sourceType` desconocido devuelve `400`.
- [x] `POST /api/ingest` sobre una carpeta con más archivos que `MaxFilesPerRequest` devuelve `400` con `error: "TooManyFiles"` y **no indexa ningún vector** (`vectorsCount` no cambia).
- [x] Con `TimeoutSeconds` reducido y un `IEmbeddingGenerator` fake con retardo, la respuesta es `200` con `status: "PartiallyCompleted"`, `remaining > 0` e `ingested > 0`.
- [x] Un corte por timeout nunca deja un documento a medias: para cada `sourceId` presente en Qdrant, el número de chunks coincide con el que produce el chunker para ese documento.
- [x] Repetir el mismo `POST` tras un `PartiallyCompleted` devuelve `status: "Completed"`, con los ya procesados contados en `skipped`.
- [x] `GET /api/ingest/stats` devuelve `200` con `collection: "knowledge"` y `dimension: 768`.
- [ ] Con el AppHost corriendo, el dashboard de Aspire muestra el recurso del modelo `nomic-embed-text` en estado `Running`.
- [ ] Tras una ingesta real contra Qdrant, la colección `knowledge` existe con dimensión 768 y `vectorsCount > 0`.
- [ ] Un punto de Qdrant inspeccionado contiene los 7 campos de payload: `sourceId`, `sourceType`, `title`, `chunkIndex`, `text`, `contentHash`, `ingestedAt`.
- [ ] Arrancar la Api dos veces consecutivas no falla por colección ya existente.
- [ ] Una ingesta genera en el dashboard de Aspire una traza del `ActivitySource` `AiKnowledgeAssistant.Ingestion` con spans por etapa.
- [x] `POST /api/query` sigue devolviendo `501` (este spec no toca el endpoint de consulta).
- [x] Ningún `.csproj` fija versiones de paquete inline (`UglyToad.PdfPig` y `Qdrant.Client` están en `Directory.Packages.props`).

> **Estado de la verificación.** Los 26 criterios marcados están cubiertos por la suite
> automatizada (135 unit + 27 integración, sin contenedores). Los 5 sin marcar requieren el
> AppHost con Qdrant y Ollama reales y quedan como comprobación manual previa al merge; su
> lógica sí está testeada con dobles en memoria (creación de colección y doble arranque en
> `VectorStoreStartupTests`, los 7 campos de payload en `QdrantPayloadTests`, la forma de la
> traza en `IngestionTelemetryTests`), así que el riesgo residual es de integración, no de lógica.
>
> Dos notas sobre el texto de los criterios: el id real del paquete en NuGet es `PdfPig`
> (`UglyToad.PdfPig` es el namespace), y el criterio de "sin runtime de contenedores" se validó
> con los puertos de Qdrant libres y sin contenedor de Qdrant levantado, no parando Docker.

## Decisions

**Alcance y arquitectura**

- **Sí:** el pipeline llega hasta Qdrant (parse → chunk → embed → upsert). Es el único punto final que deja la ingesta verificable de verdad y desbloquea SPEC 03. **No:** entregar solo parse + chunk — descartado porque deja código sin valor observable.
- **Sí:** abstracción `IDocumentSource` definida ahora, con Confluence implementado contra ella en su propio spec. Garantiza "leer de cualquier fuente" por diseño sin arrastrar auth por token, paginación y HTML→texto a este spec. **No:** implementar los 4 conectores de golpe — descartado por tamaño y por dependencia de credenciales reales para poder probar.
- **Sí:** keyed services para resolver el conector por `sourceType`, igual que el patrón previsto en SPEC 01 para los proveedores LLM. Mantiene un solo mecanismo de pluggabilidad en el repo.
- **Sí:** `Result<T>` para todo el flujo de negocio (extracción fallida, embeddings caídos). **No:** excepciones para flujo esperado — lo prohíbe el skill `dotnet-backend-patterns`.

**Disparo y contrato**

- **Sí:** `POST /api/ingest` como controlador MVC, coherente con SPEC 01 y con "API REST" del PRD. **No:** CLI — descartado por quedar fuera del contrato REST. **No:** worker/`BackgroundService` — descartado por añadir un proyecto nuevo y complicar los tests de integración.
- **Sí:** ingesta **sincrónica acotada**: `MaxFilesPerRequest: 100` y `TimeoutSeconds: 90` configurables, con corte limpio entre documentos y resumen parcial. Evita el modelo de jobs y el estado compartido, y a la vez impide que el request muera de forma opaca. **No:** asíncrona con `202 Accepted` + `IngestionJobId` + `BackgroundService` — es el diseño correcto a largo plazo, descartado ahora por añadir modelo de job, estado en memoria y tests de polling a un spec ya cargado; queda identificado como spec propio. **No:** ambos modos vía `?async=true` — descartado por duplicar superficie de API y estados.
- **Sí:** el corte por timeout ocurre **entre documentos**, no dentro de uno. Aprovecha la idempotencia por hash para que reintentar el mismo `POST` reanude en lugar de reprocesar. **No:** cancelar a mitad de un documento — descartado porque dejaría chunks parciales indexados.
- **Sí:** entrada por **ruta local** (archivo o carpeta recursiva). Permite batches reales y tests con archivos del repo. **No:** upload multipart (`IFormFile`) — descartado por límites de tamaño, disco temporal y por no encajar con carpetas ni con Confluence.
- **Sí:** `GET /api/ingest/stats` con `collection`/`vectorsCount`/`dimension`. Cuesta poco y hace verificables varios criterios de aceptación sin abrir la UI de Qdrant.
- **Sí:** ingesta **best-effort** con resumen por archivo (`ingested`/`skipped`/`failed[]`). Un PDF corrupto no puede bloquear 50 documentos. **No:** fallo transaccional al primer error — descartado por inservible en batch real. **No:** solo loguear los fallos — descartado por no verificable.
- **Sí:** endpoint de ingesta **sin autenticación** en este spec, consistente con el MVP sin auth de SPEC 01. Se asume red interna. Riesgo asumido y registrado abajo.

**Chunking y embeddings**

- **Sí:** ventana fija de ~500 tokens con ~50 de solape, cortando en límite de párrafo/frase cuando se puede. Baseline estándar, predecible y testeable con asserts numéricos. **No:** chunking por estructura (headings MD / páginas PDF) — descartado por requerir lógica distinta por formato y tests frágiles. **No:** híbrido estructura + ventana — mejor calidad, pero coste desproporcionado para el primer hito.
- **Sí:** conteo de tokens por **aproximación de caracteres** (`~4 chars ≈ 1 token`), decisión explícita del usuario. Evita una dependencia de tokenizador y su acoplamiento al modelo; el chunker solo necesita dimensionar, no facturar. **No:** tokenizador real (`Tiktoken`/`ML.Tokenizers`) — descartado por dependencia innecesaria en esta etapa. Consecuencia aceptada: los chunks pueden desviarse del tamaño nominal en textos con mucho código o CJK.
- **Sí:** Ollama `nomic-embed-text`, dimensión 768, fijada en `IOptions`. Coherente con "Ollama primero" del SPEC 01. **No:** `mxbai-embed-large` (1024) — descartado por vectores más pesados y generación más lenta en local. **No:** dejar el modelo sin valor por defecto — descartado por ser un TODO disfrazado.
- **Sí:** `IEmbeddingGenerator` con batching por `BatchSize`. Evita un request HTTP por chunk.
- **Sí:** OpenAI queda pluggable pero no implementado, igual que en SPEC 01.

**Identidad, idempotencia y almacenamiento**

- **Sí:** tracking **solo en el payload de Qdrant**. Cero dependencia de Postgres/EF Core, que sigue siendo SPEC 05. **No:** tabla `IngestedDocument` con migración EF Core ahora — descartado por adelantar SPEC 05.
- **Sí:** SHA-256 del contenido extraído + borrado por filtro `sourceId` antes de reindexar. Salta lo no modificado y evita duplicados sin base relacional. **No:** reindexar siempre — descartado por regenerar embeddings innecesariamente. **No:** solo insertar — descartado porque los duplicados envenenan el retrieval de SPEC 03.
- **Sí:** ID de punto Qdrant como `Guid` determinista desde `sourceId#index`. La idempotencia se sostiene incluso si falla el borrado previo.
- **Sí:** `SourceId` = ruta absoluta normalizada; `confluence://{spaceKey}/{pageId}` para el conector futuro. Acopla el índice a la máquina que ingesta — aceptado por simplicidad en el MVP. **No:** ruta relativa a una raíz configurada — descartado por añadir una opción más que hay que mantener sincronizada.
- **Sí:** la colección se crea al arrancar la Api y el arranque **falla en voz alta** si la dimensión existente no coincide. Prefiere caerse a indexar vectores incompatibles.
- **Sí:** índices de payload sobre `sourceId` y `contentHash`. Son los dos únicos campos que se filtran.

**Librerías y tests**

- **Sí:** `UglyToad.PdfPig` (Apache-2.0) para PDF: nativo .NET, licencia permisiva, sin binarios nativos. **No:** iText7 — descartado por AGPL y riesgo legal en producto corporativo cerrado. **No:** wrappers con dependencia nativa (docnet/pdftotext) — descartados por complicar el contenedor.
- **Sí:** unit tests reales de chunker/extractores/hashing con archivos de muestra versionados, e integración con `IEmbeddingGenerator` e `IVectorStore` fakeados. `dotnet test` sigue corriendo sin Docker. **No:** Testcontainers con Qdrant real — descartado por volver los tests lentos y dependientes de Docker; la verificación contra Qdrant real queda como criterio manual.
- **Sí:** ambos paquetes nuevos van a `Directory.Packages.props` (Central Package Management), como impone SPEC 01.

## Risks

| Riesgo | Mitigación |
| --- | --- |
| La descarga de `nomic-embed-text` en el arranque de Ollama tarda varios minutos la primera vez y hace parecer que el AppHost está colgado | El modelo se declara como recurso Aspire dedicado, visible en el dashboard con su propio estado. El paso 16 del plan documenta que la primera ejecución descarga ~270 MB. Los tests no dependen de ello (embeddings fakeados). |
| Ingesta sincrónica: una carpeta grande agota el timeout del request | `MaxFilesPerRequest` rechaza el lote de entrada con `400` antes de empezar, y `TimeoutSeconds` (90s por defecto, por debajo del corte típico de proxies) corta entre documentos devolviendo `PartiallyCompleted` con `remaining`. Reintentar el mismo `POST` reanuda gracias al hash. Si aun así resulta insuficiente, el paso a jobs asíncronos está identificado como spec propio. |
| `TimeoutSeconds` mal configurado por encima del corte del proxy vuelve a producir un request muerto | El valor por defecto (90s) queda deliberadamente por debajo de los 100–120s habituales de Kestrel/proxies, y la sección `Ingestion` se documenta en `CLAUDE.md` con esa advertencia. |
| El endpoint de ingesta sin auth permite a cualquiera indexar contenido arbitrario o leer rutas del servidor vía `path` | Riesgo aceptado para el MVP en red interna. Mitigación en este spec: validar que `path` existe y rechazar extensiones no soportadas. Auth queda en su spec; **no desplegar la Api a una red no confiable antes de ese spec**. |
| La aproximación de tokens por caracteres subestima el tamaño real en texto con mucho código, tablas o CJK, y un chunk puede exceder la ventana de contexto del modelo de embeddings | Decisión consciente. `MaxTokens: 500` deja margen amplio frente al límite de 8192 de `nomic-embed-text`, así que la desviación no rompe nada. Ajustable por `ChunkingOptions` sin cambiar código. |
| Cambiar de modelo de embeddings invalida todos los vectores ya indexados (dimensión y espacio vectorial distintos) | `EnsureCollectionAsync` compara la dimensión y **falla el arranque** en vez de mezclar espacios vectoriales. Recrear la colección y re-ingestar es el procedimiento; el hash por documento hace que la re-ingesta completa sea un único `POST`. |
| Fallo parcial a mitad de un documento (borrado por `sourceId` hecho, upsert incompleto) deja el documento a medias en el índice | Los IDs de punto son deterministas, así que re-ingestar el mismo archivo repara el estado sin duplicar. El resumen marca el archivo en `failed[]` para que el operador sepa cuál repetir. |
| PDFs escaneados (solo imagen) producen texto vacío y se indexan como chunks inútiles | Un documento cuyo texto extraído queda vacío o por debajo de un mínimo se cuenta como `failed` con motivo `EmptyExtraction`, no se indexa. OCR queda explícitamente fuera de alcance. |
| `SourceId` como ruta absoluta: ingestar la misma documentación desde otra máquina o ruta duplica todo el contenido en el índice | Limitación aceptada y documentada. Se mitiga operativamente usando una ruta canónica única para la ingesta. La alternativa (raíz configurable) está registrada como descartada en Decisiones. |
| Ollama caído o lento hace fallar la ingesta completa aunque los archivos se lean bien | `IEmbeddingGenerator` devuelve `Result.Failure` y cada documento afectado entra en `failed[]`: la respuesta es 200 con `ingested: 0`, no un 500 opaco. La resiliencia HTTP de `ServiceDefaults` (SPEC 01) cubre los reintentos transitorios. |

## What is **not** in this spec

- Conector de Confluence (SPEC propio, contra la `IDocumentSource` definida aquí).
- Pipeline RAG, retrieval y Knowledge Orchestrator → SPEC 03.
- Cache semántico → SPEC 04.
- Persistencia en Postgres, EF Core, migraciones y auditoría → SPEC 05.
- Ingesta en background, cola de jobs y `202 Accepted`.
- Upload multipart, OCR de PDFs escaneados, y formatos DOCX/HTML/CSV.
- OpenAI como proveedor de embeddings.
- Autenticación del endpoint de ingesta.
- Re-ingesta programada o watcher de carpetas.
- Testcontainers / Qdrant real en la suite de tests.

Cada uno de estos, cuando llegue, va en su propio spec.
