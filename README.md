# AI Knowledge Assistant

Plataforma de consulta de conocimiento corporativo basada en IA: responde preguntas en lenguaje
natural utilizando la documentación interna de la organización mediante RAG (Retrieval-Augmented
Generation).

El requisito autoritativo es [`references/PRD.md`](references/PRD.md). Las decisiones de diseño y su
justificación viven en los specs de [`specs/`](specs/); las convenciones de código, en
[`CLAUDE.md`](CLAUDE.md).

## Estado actual

| Spec | Alcance | Estado |
| --- | --- | --- |
| 01 | Scaffolding de la solución .NET Aspire, capas Clean Architecture, recursos y telemetría | ✅ Implementado |
| 02 | Pipeline de ingesta de documentos: PDF/TXT/Markdown → chunks → embeddings → Qdrant | ✅ Implementado |
| — | Conector de Confluence | ⏳ Pendiente |
| 03 | Pipeline RAG, retrieval y Knowledge Orchestrator | ⏳ Pendiente |
| 04 | Cache semántico | ⏳ Pendiente |
| 05 | Persistencia en PostgreSQL: preguntas/respuestas, feedback y auditoría | ⏳ Pendiente |

Hoy se puede **ingerir documentación y consultar el estado del índice**. `POST /api/query` sigue
devolviendo `501`: la generación de respuestas llega en el SPEC 03. Hasta el SPEC 05 no hay
almacenamiento relacional — el único registro de lo ingerido es el payload de Qdrant.

## Arquitectura objetivo (PRD §7)

```
Usuario → API → Knowledge Orchestrator → Cache semántico → RAG → Qdrant → LLM → Persistencia → Respuesta
```

El **Knowledge Orchestrator** es la pieza central: consulta primero el cache semántico y, si no hay
acierto, cae al pipeline RAG (recuperar de Qdrant → construir prompt fundamentado → LLM), persiste el
par pregunta/respuesta y responde. El orden cache-antes-de-RAG es lo que sostiene los objetivos de
rendimiento del PRD (<1s en acierto de cache, <5s en el camino RAG completo).

## Stack

- **C# / .NET 10 + .NET Aspire 13.4** — orquestación, service defaults y telemetría.
- **Qdrant** — almacén vectorial de embeddings.
- **Ollama** — proveedor de embeddings (`nomic-embed-text`, 768 dimensiones) y, más adelante, de LLM.
  OpenAI queda enchufable vía keyed services, sin implementar.
- **PostgreSQL** — persistencia de Q/A, feedback y auditoría (SPEC 05).
- **OpenTelemetry** — observabilidad, requisito no funcional duro.
- **PdfPig** — extracción de texto de PDF (Apache-2.0, sin dependencias nativas).

## Estructura

```
AiKnowledgeAssistant.Domain           # entidades y Result<T>; sin dependencias
AiKnowledgeAssistant.Application      # casos de uso, abstracciones y configuración tipada
AiKnowledgeAssistant.Infrastructure   # conectores, Ollama, Qdrant, registro DI
AiKnowledgeAssistant.Api              # controladores MVC
AiKnowledgeAssistant.AppHost          # orquestación Aspire de api/postgres/qdrant/ollama
AiKnowledgeAssistant.ServiceDefaults  # telemetría, health checks, resiliencia, service discovery
AiKnowledgeAssistant.UnitTests        # xUnit + Moq, con corpus de muestra en Samples/
AiKnowledgeAssistant.IntegrationTests # WebApplicationFactory con dobles en memoria
```

Capas: `Domain` → `Application` → `Infrastructure`, con `Api` sobre `Application`, `Infrastructure` y
`ServiceDefaults`. Las versiones de paquete están centralizadas en `Directory.Packages.props`; no se
añaden `Version=` en los `.csproj`.

## Puesta en marcha

Requisitos: **SDK de .NET 10** y un runtime de contenedores (Docker o Podman).

```bash
dotnet build AiKnowledgeAssistant.sln
dotnet test                                              # no necesita contenedores
dotnet run --project AiKnowledgeAssistant.AppHost        # dashboard de Aspire + recursos
```

El AppHost levanta `api`, `postgres`, `qdrant`, `ollama` y el recurso del modelo `embedding`. **La
primera ejecución descarga `nomic-embed-text` (~270 MB)**, así que ese recurso permanece en
`Starting` unos minutos. La API no espera por él: una ingesta lanzada durante esa ventana devuelve
`200` con los documentos en `failed[]` y motivo `EmbeddingRequestFailed`, y basta reintentarla.

## Endpoints

### `POST /api/ingest`

Ingiere un archivo o una carpeta local (recorrido recursivo).

```jsonc
// petición — sourceType es opcional: si se omite, se infiere por extensión;
// si se indica (pdf | txt | markdown), solo se ingieren archivos de ese tipo
{ "path": "C:\\docs\\handbook", "sourceType": "markdown" }
```

```jsonc
// respuesta 200
{
  "status": "Completed",        // o "PartiallyCompleted" si se agotó el tiempo
  "totalFiles": 50, "ingested": 45, "skipped": 2, "failedCount": 3,
  "remaining": 0, "chunksIndexed": 812, "durationMs": 41230,
  "failed": [ { "path": "...", "reason": "PdfExtractionFailed: ..." } ]
}
```

Devuelve `400` si la ruta no existe, el `sourceType` es desconocido, no hay archivos soportados, o el
lote supera el límite de archivos (`{ "error": "TooManyFiles", "maxFilesPerRequest": 100 }`).

### `GET /api/ingest/stats`

```jsonc
{ "collection": "knowledge", "vectorsCount": 812, "dimension": 768 }
```

### Otros

`POST /api/query` responde `501` (SPEC 03). `/health` y `/alive` provienen de Aspire y solo se
publican en el entorno de desarrollo.

## Cómo se comporta la ingesta

Conviene conocer cuatro propiedades antes de usarla en serio:

- **Idempotente.** Se calcula el SHA-256 del texto extraído: un documento sin cambios se salta y no
  regenera embeddings. Los IDs de punto en Qdrant son GUID deterministas derivados de
  `sourceId#index`, así que re-ingerir sobrescribe en lugar de duplicar. Si un documento cambia, sus
  chunks anteriores se borran antes de reindexar.
- **Best-effort.** Un PDF corrupto no bloquea a los otros cuarenta y nueve documentos: el fallo se
  reporta en `failed[]` y el lote continúa. Una ejecución devuelve `200` incluso con `ingested: 0`.
- **Sincrónica y acotada.** Un lote por encima de `MaxFilesPerRequest` se rechaza sin indexar nada.
  Al agotarse `TimeoutSeconds`, el bucle se detiene **entre documentos** (nunca a mitad de uno) y
  responde `PartiallyCompleted` con `remaining`. Repetir la misma petición **reanuda** el trabajo,
  porque lo ya almacenado cuenta como `skipped`.
- **Acoplada a la máquina que ingiere.** El `sourceId` es la ruta absoluta normalizada, así que
  ingerir la misma documentación desde otra ruta duplica el contenido en el índice. Limitación
  asumida para el MVP: usa una ruta canónica única.

## Configuración

Los valores por defecto están en `AiKnowledgeAssistant.Api/appsettings.json`.

| Sección | Claves (por defecto) |
| --- | --- |
| `Embeddings` | `Model` (`nomic-embed-text`), `Dimension` (`768`), `BatchSize` (`16`) |
| `Chunking` | `MaxTokens` (`500`), `OverlapTokens` (`50`) |
| `VectorStore` | `CollectionName` (`knowledge`), `Distance` (`Cosine`) |
| `Ingestion` | `MaxFilesPerRequest` (`100`), `TimeoutSeconds` (`90`) |

Dos avisos que ahorran depuración:

- Mantén `Ingestion:TimeoutSeconds` **por debajo** del corte de petición de Kestrel y de cualquier
  proxy delante (habitualmente 100–120s). Si no, un lote lento muere en la conexión en lugar de
  devolver su resumen parcial.
- Cambiar `Embeddings:Model` o `Dimension` **invalida todos los vectores indexados**. La aplicación se
  niega a arrancar contra una colección de dimensión distinta, en vez de mezclar espacios vectoriales:
  el procedimiento es recrear la colección y re-ingerir.

## Observabilidad

El pipeline emite trazas y métricas bajo el nombre `AiKnowledgeAssistant.Ingestion`, visibles en el
dashboard de Aspire:

- Spans `ingest.batch` → `ingest.document` → `ingest.extract` / `chunk` / `embed` / `upsert`.
- Contadores de documentos ingeridos, saltados y fallidos, chunks indexados, y un histograma de
  duración del lote.

## Tests

`dotnet test` no requiere contenedores. Los tests de integración levantan la API real —controlador,
handler, locator, extractores y chunker— y sustituyen únicamente las dos dependencias salientes
(Qdrant y Ollama) por dobles en memoria. El corpus de muestra vive en
`AiKnowledgeAssistant.UnitTests/Samples/` e incluye casos incómodos a propósito: un PDF válido, un PDF
corrupto, un archivo vacío, BOM UTF-8, UTF-16 y una extensión no soportada.

## Seguridad

**El endpoint de ingesta no tiene autenticación** en el estado actual: se asume red interna y la
autenticación tiene su propio spec pendiente. El parámetro `path` lee rutas arbitrarias del servidor,
así que **no despliegues la API en una red no confiable** antes de ese spec.

## Contribuir

El trabajo de features pasa por el flujo `/spec-impl` (configurado en `specs/.spec-config.yml`): un
spec se escribe, se aprueba y se implementa paso a paso en una rama `spec-NN-slug`. Cambios de alcance
van al spec, no al código por sorpresa. El estilo de C# lo fija el skill `dotnet-backend-patterns`
descrito en [`CLAUDE.md`](CLAUDE.md).
