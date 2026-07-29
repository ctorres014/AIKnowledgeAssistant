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
| 03 | Pipeline RAG, retrieval y Knowledge Orchestrator: `POST /api/query` | ✅ Implementado |
| — | Conector de Confluence | ⏳ Pendiente |
| 04 | Cache semántico | ⏳ Pendiente |
| 05 | Persistencia en PostgreSQL: preguntas/respuestas, feedback y auditoría | ⏳ Pendiente |

Hoy se puede **ingerir documentación y preguntar sobre ella en lenguaje natural**. El camino completo
funciona de punta a punta: `POST /api/query` embebe la pregunta, recupera los chunks relevantes de
Qdrant y genera con Ollama una respuesta fundamentada **únicamente** en ese contexto, con sus
citaciones. Si nada supera el umbral de similitud responde `foundAnswer: false` **sin llamar al
modelo**, en vez de inventar.

Todavía **no hay cache semántico** (SPEC 04) ni almacenamiento relacional (SPEC 05): una consulta no
deja rastro más allá de las trazas de OpenTelemetry, y el único registro de lo ingerido es el payload
de Qdrant.

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
- **Ollama** — proveedor de embeddings (`nomic-embed-text`, 768 dimensiones) y de generación
  (`llama3.2:3b` en desarrollo). OpenAI queda enchufable vía keyed services, sin implementar.
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

El AppHost levanta `api`, `postgres`, `qdrant`, `ollama` y **dos recursos de modelo**: `embedding`
(`nomic-embed-text`) y `chat` (`llama3.2:3b`). **La primera ejecución descarga ambos** (~270 MB y
~2 GB), así que permanecen en `Starting` unos minutos. La API no espera por ellos:

- una ingesta lanzada en esa ventana devuelve `200` con los documentos en `failed[]` y motivo
  `EmbeddingRequestFailed`;
- una consulta devuelve `503 LlmUnavailable`.

En ambos casos basta reintentar cuando el dashboard marque el recurso como `Running`.

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

### `POST /api/query`

Responde una pregunta en lenguaje natural a partir del corpus indexado.

```jsonc
// petición — includeChunks es opcional (false por defecto)
{ "question": "¿Cuánto dura el onboarding?", "includeChunks": false }
```

```jsonc
// respuesta 200 con respuesta fundamentada
{
  "answer": "El onboarding dura cinco jornadas...",
  "foundAnswer": true, "model": "llama3.2:3b", "durationMs": 12480,
  "citations": [ { "title": "handbook-onboarding",
                   "sourceId": "c:/docs/handbook-onboarding.md",
                   "sourceType": "Markdown", "chunkIndex": 3, "score": 0.82 } ]
}
```

```jsonc
// respuesta 200 sin coincidencias por encima de Rag:MinScore — no se llama al LLM
{ "answer": null, "foundAnswer": false, "model": "llama3.2:3b",
  "durationMs": 210, "citations": [] }
```

Con `"includeChunks": true` cada citación añade su `"text"`: el fragmento exacto que recibió el
modelo. Es una ayuda de depuración —sirve para ver los `score` reales y ajustar `Rag:MinScore`— y
está desactivada por defecto porque el endpoint **no tiene autenticación**.

| Código | Cuándo |
| --- | --- |
| `400` | `question` vacía, en blanco o ausente |
| `503` | `{ "error": "LlmUnavailable" }` (Ollama caído o aún descargando el modelo), `{ "error": "VectorSearchFailed" }` (Qdrant inalcanzable), `{ "error": "EmbeddingRequestFailed" }` |
| `504` | `{ "error": "LlmTimeout" }` — la generación supera `Rag:TimeoutSeconds` |

### Otros

`/health` y `/alive` provienen de Aspire y solo se publican en el entorno de desarrollo.

## Cómo se comporta la consulta

```
pregunta → embedding → búsqueda en Qdrant (TopK + MinScore) → prompt fundamentado → LLM → respuesta
```

- **Fundamentada o nada.** El prompt de sistema ordena responder **solo** con el contexto recuperado,
  admitir explícitamente cuando no alcanza, y contestar en el idioma de la pregunta.
  `Rag:Temperature` es `0.2` a propósito: FR-003 pide respuestas fundamentadas, no creativas.
- **Sin contexto no se genera.** Si ningún chunk supera `Rag:MinScore`, la respuesta es `200` con
  `foundAnswer: false` y `citations: []`, sin gastar la llamada más cara del pipeline en decir que no.
- **Cada consulta es independiente.** No hay historial ni conversación multi-turno; el PRD lo excluye
  del MVP.
- **Las citaciones no van correlacionadas** con frases concretas de la respuesta: son el conjunto de
  fragmentos que la fundamentaron, sin marcadores `[1]`/`[2]` en línea.
- **Retrieval puramente vectorial**, sobre todo el índice y sin reranking ni búsqueda por palabra
  clave. Las preguntas que dependen de terminología exacta (códigos, nombres de campo) son el punto
  débil conocido.

### Modelo de generación: desarrollo (CPU) vs. objetivo (GPU)

El modelo es configuración (`Rag:Model`), nunca código:

| Entorno | Modelo | Razón |
| --- | --- | --- |
| Desarrollo (CPU) | `llama3.2:3b` | ~2 GB; responde en 10–20s en un portátil sin GPU. Es el valor por defecto de `appsettings.json`. |
| Producción (GPU) | `qwen2.5:7b` | Modelo objetivo, mejor calidad. Requiere GPU con CUDA o ROCm para acercarse al `<5s` del PRD; en CPU una consulta ronda 1–2 minutos y agotaría el timeout de 60s. |

Cambiar entre ambos es editar `appsettings.json` (y el tag del recurso `chat` en el AppHost). **El
objetivo de `<5s` del PRD no es verificable en CPU** y por eso no figura entre los criterios de
aceptación del SPEC 03: lo que sí se garantiza es que la consulta termina dentro de
`Rag:TimeoutSeconds` o devuelve `504`.

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
| `Rag` | `Model` (`llama3.2:3b`), `TopK` (`5`), `MinScore` (`0.5`), `TimeoutSeconds` (`60`), `MaxAnswerTokens` (`500`), `Temperature` (`0.2`) |

`TopK` y `MinScore` son fijos por configuración: no se pueden sobrescribir desde la petición, para
mantener la API pequeña y para que el cache semántico del SPEC 04 no tenga que versionar sus entradas
por combinación de parámetros.

Tres avisos que ahorran depuración:

- Mantén `Ingestion:TimeoutSeconds` **por debajo** del corte de petición de Kestrel y de cualquier
  proxy delante (habitualmente 100–120s). Si no, un lote lento muere en la conexión en lugar de
  devolver su resumen parcial.
- Cambiar `Embeddings:Model` o `Dimension` **invalida todos los vectores indexados**. La aplicación se
  niega a arrancar contra una colección de dimensión distinta, en vez de mezclar espacios vectoriales:
  el procedimiento es recrear la colección y re-ingerir.
- **`Rag:MinScore: 0.5` es un punto de partida sin validar.** Modelos como `nomic-embed-text` dan
  scores altos incluso entre textos sin relación, así que el umbral puede resultar permisivo. Lanza
  una consulta con respuesta clara en el corpus y otra deliberadamente ajena, ambas con
  `includeChunks: true`, y fija el valor con esos números.

## Observabilidad

Dos fuentes de trazas y métricas, visibles en el dashboard de Aspire:

`AiKnowledgeAssistant.Ingestion`

- Spans `ingest.batch` → `ingest.document` → `ingest.extract` / `chunk` / `embed` / `upsert`.
- Contadores de documentos ingeridos, saltados y fallidos, chunks indexados, y un histograma de
  duración del lote.

`AiKnowledgeAssistant.Rag`

- Spans `query` → `query.embed` / `query.search` / `query.generate`, con atributos de `topK`,
  `minScore`, número de chunks recuperados, score máximo, modelo y código de error.
- Contadores de consultas respondidas, sin resultados y fallidas, más un histograma de duración
  etiquetado por etapa (`embed`, `search`, `generate`, `total`).

Hasta el SPEC 05 esta traza es **el único registro que deja una consulta**, y es efímera: si una
respuesta sale mal, el diagnóstico hay que hacerlo en caliente desde el dashboard.

## Tests

`dotnet test` no requiere contenedores. Los tests de integración levantan la API real —controlador,
orquestador, pipeline, handler, locator, extractores y chunker— y sustituyen únicamente las
dependencias salientes (Qdrant, los embeddings y el LLM) por dobles en memoria. El corpus de muestra
vive en
`AiKnowledgeAssistant.UnitTests/Samples/` e incluye casos incómodos a propósito: un PDF válido, un PDF
corrupto, un archivo vacío, BOM UTF-8, UTF-16 y una extensión no soportada.

## Seguridad

**Ningún endpoint tiene autenticación** en el estado actual: se asume red interna y la autenticación
tiene su propio spec pendiente. El parámetro `path` de la ingesta lee rutas arbitrarias del servidor,
y `POST /api/query` con `includeChunks: true` devuelve el texto íntegro de los fragmentos indexados,
así que **no despliegues la API en una red no confiable** antes de ese spec.

## Contribuir

El trabajo de features pasa por el flujo `/spec-impl` (configurado en `specs/.spec-config.yml`): un
spec se escribe, se aprueba y se implementa paso a paso en una rama `spec-NN-slug`. Cambios de alcance
van al spec, no al código por sorpresa. El estilo de C# lo fija el skill `dotnet-backend-patterns`
descrito en [`CLAUDE.md`](CLAUDE.md).
