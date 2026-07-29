# SPEC 03 — Pipeline RAG, retrieval y Knowledge Orchestrator

> **Status:** Aprobado
> **Depends on:** SPEC 01, SPEC 02
> **Date:** 2026-07-28
> **Objective:** Implementar el pipeline RAG y el Knowledge Orchestrator que responden una
> pregunta en lenguaje natural recuperando los chunks relevantes de Qdrant y generando con
> Ollama una respuesta fundamentada **únicamente** en ese contexto, expuesto vía
> `POST /api/query`.

Cubre **FR-001** (consulta en lenguaje natural) y **FR-003** (respuestas únicamente con
contexto recuperado) del PRD. FR-004/005/006/007 —persistencia, reutilización semántica,
feedback y auditoría— siguen en SPEC 04 y SPEC 05.

**Modelo de generación.** El modelo es configuración (`Rag:Model`), no código:

| Entorno | Modelo | Razón |
| --- | --- | --- |
| Desarrollo (CPU) | `llama3.2:3b` | ~2 GB; respuesta en 10–20s en portátil sin GPU. Es el valor por defecto de `appsettings.json` y el que hace verificable este spec. |
| Producción (GPU) | `qwen2.5:7b` | Modelo objetivo, mejor calidad. Requiere GPU con CUDA/ROCm para acercarse al <5s del PRD. |

Cambiar entre ambos es editar `appsettings.json`. El objetivo de latencia <5s del PRD **no es
verificable en CPU** y así queda reflejado en los criterios de aceptación.

## Scope

**In:**

- Abstracción `ILlmClient` en `Application` + `OllamaLlmClient` en `Infrastructure` contra
  `/api/chat` de Ollama, registrado como **keyed service** `"ollama"` y como default, con el
  mismo patrón que `IEmbeddingGenerator` en SPEC 02.
- **Nuevo método `SearchAsync` en `IVectorStore`** (búsqueda por vector con `TopK` y `MinScore`),
  implementado en `QdrantVectorStore` y en los dobles en memoria de los tests.
- Tipo de dominio `ScoredChunk` (chunk recuperado + score) y `Answer` como resultado del pipeline.
- **`KnowledgeOrchestrator`** en `Application`: la pieza central del PRD §7. En este spec ejecuta
  cache (vacío) → RAG → persistencia (vacía), con las dos costuras declaradas explícitamente para
  que SPEC 04 y SPEC 05 las rellenen sin tocar el controlador.
- Pipeline RAG (`RagPipeline` / `AnswerQuestionHandler`): embeber la pregunta con el
  `IEmbeddingGenerator` existente → `SearchAsync` con `TopK`/`MinScore` → construir prompt
  fundamentado → generar con el LLM → devolver respuesta + citaciones.
- **Constructor de prompt** como tipo propio y testeable (`GroundedPromptBuilder`): instrucción de
  responder **solo** con el contexto dado, de decir que no lo sabe si el contexto no alcanza, y de
  responder **en el idioma de la pregunta**.
- **Camino sin resultados**: si tras aplicar `MinScore` no queda ningún chunk, se devuelve
  `200` con `foundAnswer: false`, `answer: null` y `citations: []` **sin llamar al LLM**.
- Configuración tipada `RagOptions` (sección `Rag`): `Model`, `TopK`, `MinScore`,
  `TimeoutSeconds`, `MaxAnswerTokens`, `Temperature`. Sin override por request.
- `QueryController`: `POST /api/query` real, sustituyendo el `501`. Request
  `{ "question": "...", "includeChunks": false }`; respuesta con `answer`, `foundAnswer`,
  `citations[]`, `model` y `durationMs`.
- Citaciones con `title`, `sourceId`, `sourceType`, `chunkIndex` y `score`; el `text` del chunk
  solo cuando el request pide `includeChunks: true` (depuración).
- Mapeo de fallos: `503 LlmUnavailable` (Ollama caído o modelo aún descargando),
  `504 LlmTimeout` (supera `Rag:TimeoutSeconds`), `400` para pregunta vacía.
- AppHost: segundo recurso de modelo Ollama para generación, referenciado por la Api.
- Telemetría OpenTelemetry: `ActivitySource` `AiKnowledgeAssistant.Rag` con spans
  `query` → `query.embed` / `query.search` / `query.generate`, y contadores de consultas
  respondidas, sin resultados y fallidas, más histograma de duración por etapa.
- Tests: unit del constructor de prompt, del filtrado por `MinScore` y del orquestador con dobles;
  integración de `POST /api/query` con `IEmbeddingGenerator`, `IVectorStore` e `ILlmClient`
  en memoria — **`dotnet test` sigue sin necesitar Docker**.
- Documentación: `README.md` y `CLAUDE.md` con el endpoint, la sección `Rag` y la tabla
  **modelo de desarrollo (CPU) vs. modelo objetivo (GPU)**.

**Out of scope (para specs futuros):**

- **Cache semántico** → SPEC 04. El orquestador deja la costura; aquí no hay cache de ningún tipo.
- **Persistencia de preguntas/respuestas, feedback y auditoría** (FR-004/006/007) → SPEC 05.
  Una consulta no deja rastro más allá de las trazas de OpenTelemetry.
- Streaming SSE token a token.
- Conversación multi-turno e historial: cada consulta es independiente (el PRD lo excluye del MVP).
- **Reranking** de los chunks recuperados y búsqueda híbrida (vectorial + keyword/BM25).
- **Reescritura o expansión de la pregunta** antes de embeberla.
- Filtrado del retrieval por `sourceType` u otros metadatos desde el request: la búsqueda es
  general sobre todo el índice.
- Override de `TopK`/`MinScore` por request.
- **Marcadores de citación en línea** en el texto de la respuesta (`[1]`, `[2]`): las citaciones
  van en un array aparte, sin correlación con frases concretas.
- OpenAI como proveedor de LLM: pluggable por diseño, no implementado.
- Autenticación del endpoint de consulta.
- Arnés de evaluación de calidad del RAG (precisión, recall, groundedness) y tuning de `TopK`
  y `MinScore` con métricas.
- Conector de Confluence (spec propio).

## Data model

No hay tablas ni EF Core: la persistencia relacional sigue siendo SPEC 05. Este spec introduce
tipos de dominio nuevos y **modifica un contrato existente** (`IVectorStore`).

### Dominio (`AiKnowledgeAssistant.Domain/Rag/`)

```csharp
// RetrievedChunk.cs — un chunk devuelto por la búsqueda vectorial, con su score
public sealed record RetrievedChunk(
    string SourceId,
    SourceType SourceType,
    string Title,
    int ChunkIndex,
    string Text,
    float Score);

// Answer.cs — resultado del pipeline, antes de mapearse a DTO
public sealed record Answer(
    string? Text,                              // null cuando FoundAnswer es false
    bool FoundAnswer,
    IReadOnlyList<RetrievedChunk> Citations,   // vacío cuando FoundAnswer es false
    string Model,
    long DurationMs);
```

`RetrievedChunk` es **deliberadamente distinto de `DocumentChunk`**: el payload de Qdrant no
guarda `TokenCount` ni el `Id` del punto, y en cambio aporta `Score`, que no existe en ingesta.
Forzar la reutilización obligaría a inventar valores.

### Application — contratos

```csharp
// Application/Abstractions/ILlmClient.cs
public interface ILlmClient
{
    /// <summary>Modelo configurado, propagado a la respuesta y a las trazas.</summary>
    string Model { get; }

    Task<Result<LlmCompletion>> CompleteAsync(LlmPrompt prompt, CancellationToken ct);
}

public sealed record LlmPrompt(string System, string User);

public sealed record LlmCompletion(string Text, int? PromptTokens, int? CompletionTokens);

// Application/Abstractions/IVectorStore.cs — MÉTODO AÑADIDO al contrato de SPEC 02
Task<Result<IReadOnlyList<RetrievedChunk>>> SearchAsync(
    float[] queryVector, int topK, float minScore, CancellationToken ct);
```

Códigos de `Result.Failure` que el controlador traduce a HTTP:

| Código | Origen | HTTP |
| --- | --- | --- |
| `LlmUnavailable` | Ollama inalcanzable, modelo aún descargando, error HTTP del proveedor | `503` |
| `LlmTimeout` | La generación supera `Rag:TimeoutSeconds` | `504` |
| `EmbeddingRequestFailed` | Reutilizado de SPEC 02 al embeber la pregunta | `503` |
| `VectorSearchFailed` | Qdrant inalcanzable o error de búsqueda | `503` |

### Application — configuración tipada

```csharp
// Application/Rag/RagOptions.cs — sección "Rag"
public sealed class RagOptions
{
    public string Model { get; init; } = "llama3.2:3b";  // ver cabecera: dev CPU vs. objetivo GPU
    public int TopK { get; init; } = 5;
    public float MinScore { get; init; } = 0.5f;
    public int TimeoutSeconds { get; init; } = 60;
    public int MaxAnswerTokens { get; init; } = 500;
    public float Temperature { get; init; } = 0.2f;
}
```

`Temperature: 0.2` es baja a propósito: FR-003 pide respuestas fundamentadas, no creativas.

### Prompt fundamentado (`GroundedPromptBuilder`)

```
System:
  Eres un asistente de conocimiento corporativo. Responde ÚNICAMENTE con la información
  del CONTEXTO. Si el contexto no contiene la respuesta, dilo explícitamente y no uses
  conocimiento propio. Responde en el mismo idioma en el que está formulada la pregunta.

User:
  CONTEXTO:
  [1] {title} (fragmento {chunkIndex})
  {text}

  [2] {title} (fragmento {chunkIndex})
  {text}

  PREGUNTA: {question}
```

### Contratos HTTP

```
POST /api/query
Request: { "question": "¿Cuánto dura el onboarding?", "includeChunks": false }

Response 200 (con respuesta):
{ "answer": "El onboarding dura cinco jornadas...",
  "foundAnswer": true, "model": "llama3.2:3b", "durationMs": 12480,
  "citations": [ { "title": "handbook-onboarding",
                   "sourceId": "c:/docs/handbook-onboarding.md",
                   "sourceType": "Markdown", "chunkIndex": 3, "score": 0.82 } ] }

Response 200 (sin resultados por encima de MinScore — no se llama al LLM):
{ "answer": null, "foundAnswer": false, "model": "llama3.2:3b",
  "durationMs": 210, "citations": [] }

Response 200 (includeChunks: true — cada citación añade su "text"):
{ ..., "citations": [ { ..., "text": "El onboarding de nuevos empleados se estructura en..." } ] }

Response 400: pregunta vacía o ausente
Response 503: { "error": "LlmUnavailable" }   |   { "error": "VectorSearchFailed" }
Response 504: { "error": "LlmTimeout" }
```

`includeChunks` es una decisión de **presentación**: el pipeline siempre devuelve el texto en
`Answer.Citations`, y el controlador decide si lo serializa.

## Implementation plan

1. **Dominio de consulta.** Crear `Domain/Rag/`: `RetrievedChunk` y `Answer`.
   *Verificación:* `dotnet build` compila; `Domain` sigue sin referenciar ningún otro proyecto.

2. **Abstracciones y configuración.** En `Application`: `ILlmClient`, `LlmPrompt`,
   `LlmCompletion` y `Application/Rag/RagOptions.cs`. Añadir la sección `Rag` a
   `appsettings.json` con los valores por defecto (`llama3.2:3b`, `TopK: 5`, `MinScore: 0.5`,
   `TimeoutSeconds: 60`, `MaxAnswerTokens: 500`, `Temperature: 0.2`).
   *Verificación:* compila; `Application` sigue referenciando solo `Domain`.

3. **Búsqueda en el vector store.** Añadir `SearchAsync` a `IVectorStore`, implementarla en
   `QdrantVectorStore` (`SearchAsync` del cliente con `limit: topK`, `scoreThreshold: minScore`
   y payload completo → `RetrievedChunk`), y **actualizar los dobles en memoria** de
   `IntegrationTests` para que la solución siga compilando.
   *Verificación:* unit tests del mapeo payload → `RetrievedChunk` con cliente mockeado, y de
   que un error del cliente devuelve `Result.Failure("VectorSearchFailed")` en vez de propagar
   la excepción. `dotnet test` sigue en verde.

4. **Constructor de prompt.** `GroundedPromptBuilder` en `Application/Rag/`: bloque de sistema
   con las tres reglas (solo contexto, admitir desconocimiento, idioma de la pregunta) y bloque
   de usuario con los chunks numerados y la pregunta.
   *Verificación:* unit tests — el prompt contiene el texto de los N chunks en orden y con su
   título; con 0 chunks el builder no se invoca (lo cubre el paso 6).

5. **Cliente LLM de Ollama.** `OllamaLlmClient : ILlmClient` en `Infrastructure/Rag/` con
   `HttpClient` tipado contra `/api/chat` (`stream: false`, `options.temperature`,
   `options.num_predict = MaxAnswerTokens`), `CancellationToken` propagado y un
   `CancellationTokenSource` con `CancelAfter(TimeoutSeconds)`. Mapeo de fallos: excepción de
   conexión o HTTP no exitoso → `Result.Failure("LlmUnavailable")`; cancelación por el token
   propio → `Result.Failure("LlmTimeout")`.
   *Verificación:* unit tests con `HttpMessageHandler` mockeado — respuesta OK devuelve el texto;
   `500` devuelve `LlmUnavailable`; handler que tarda más que el timeout devuelve `LlmTimeout`;
   ninguno de los tres lanza excepción.

6. **Pipeline RAG.** `RagPipeline` en `Application/Rag/`: embeber la pregunta →
   `SearchAsync(topK, minScore)` → si la lista queda **vacía**, devolver
   `Answer(null, FoundAnswer: false, [], model, durationMs)` **sin llamar al LLM** → si no,
   construir prompt, generar y devolver la respuesta con sus citaciones.
   *Verificación:* unit tests con dobles — sin chunks por encima del umbral, el `ILlmClient`
   **no recibe ninguna llamada** y `FoundAnswer` es `false`; con chunks, la respuesta lleva
   tantas citaciones como chunks recuperados; un fallo del embedder o de la búsqueda se propaga
   como `Result.Failure` con su código.

7. **Knowledge Orchestrator.** `KnowledgeOrchestrator` en `Application/Rag/`, punto de entrada
   único del caso de uso: consultar cache (costura vacía, SPEC 04) → delegar en `RagPipeline` →
   persistir (costura vacía, SPEC 05) → devolver `Result<Answer>`. Las costuras quedan como
   métodos privados documentados con el spec que las rellenará, no como TODOs sueltos.
   *Verificación:* unit test — el orquestador delega en el pipeline y devuelve su resultado tal
   cual; el controlador depende **solo** del orquestador, nunca del pipeline.

8. **Registro DI.** `AddRag(this IHostApplicationBuilder)` en `Infrastructure`: `IOptions<RagOptions>`,
   `HttpClient` tipado de Ollama para chat, `ILlmClient` keyed `"ollama"` + default,
   `GroundedPromptBuilder`, `RagPipeline` y `KnowledgeOrchestrator`. Invocarlo desde `Program.cs`.
   *Verificación:* la Api arranca y resuelve `KnowledgeOrchestrator` sin errores de DI.

9. **Endpoint real.** Reescribir `QueryController`: `POST /api/query` con `QueryRequest(string
   Question, bool IncludeChunks = false)`; pregunta vacía o en blanco → `400`; mapeo de códigos
   de fallo a `503`/`504`; serialización de citaciones con `text` solo si `includeChunks`.
   *Verificación:* el `501` desaparece; la verificación completa llega en el paso 12.

10. **AppHost.** Añadir el modelo de generación como recurso Ollama propio
    (`ollama.AddModel("chat", "llama3.2:3b")`) y referenciarlo desde la Api, junto al recurso
    `embedding` ya existente.
    *Verificación:* el dashboard muestra los dos recursos de modelo y la Api resuelve su endpoint.

11. **Telemetría.** `RagTelemetry` con `ActivitySource` y `Meter` `AiKnowledgeAssistant.Rag`:
    spans `query` → `query.embed` / `query.search` / `query.generate`, atributos de `topK`,
    número de chunks recuperados, score máximo y modelo; contadores de consultas respondidas,
    sin resultados y fallidas, más histograma de duración. Registrar la fuente en `ServiceDefaults`.
    *Verificación:* unit test de la forma de la traza con un `ActivityListener`, al estilo de
    `IngestionTelemetryTests`.

12. **Tests de integración.** En `IntegrationTests`, añadir un `ILlmClient` en memoria junto a
    los dobles ya existentes: consulta con índice poblado → `200` con `foundAnswer: true` y
    citaciones; consulta sin coincidencias → `200` con `foundAnswer: false`, `answer: null` y
    **cero llamadas al LLM**; `includeChunks: true` → las citaciones traen `text`; pregunta vacía
    → `400`; doble que falla con `LlmUnavailable` → `503`; doble que agota el timeout → `504`.
    *Verificación:* `dotnet test` pasa completo **sin runtime de contenedores**.

13. **Documentación.** Actualizar `README.md` (tabla de estado con SPEC 03 implementado, endpoint
    `POST /api/query` con sus respuestas, sección `Rag` de configuración y **la tabla de modelo
    de desarrollo en CPU vs. modelo objetivo en GPU con su justificación de latencia**) y
    `CLAUDE.md` (estado del proyecto, tipos por capa, endpoints, configuración y el aviso de que
    `SearchAsync` es el punto donde SPEC 03 modifica el contrato de SPEC 02).
    *Verificación:* los endpoints y claves de configuración documentados coinciden con el código.

## Acceptance criteria

**Compilación y capas**

- [x] `dotnet build AiKnowledgeAssistant.sln` compila sin errores ni warnings de versión de paquete.
- [x] `Domain` sigue sin referenciar ningún otro proyecto de la solución.
- [x] `Application` referencia únicamente `Domain`: `OllamaLlmClient` y el cliente de Qdrant
      viven solo en `Infrastructure`.
- [x] `dotnet test` pasa completo **sin un runtime de contenedores activo**.
- [x] Ningún `.csproj` fija versiones de paquete inline.

**Retrieval**

- [x] `QdrantVectorStore.SearchAsync` mapea los 5 campos de payload (`sourceId`, `sourceType`,
      `title`, `chunkIndex`, `text`) más el score a `RetrievedChunk`.
- [x] `SearchAsync` con `topK: 5` nunca devuelve más de 5 elementos.
- [x] `SearchAsync` no devuelve ningún chunk con `Score < MinScore`.
- [x] Un error del cliente de Qdrant devuelve `Result.Failure("VectorSearchFailed")` y **no**
      lanza excepción.

**Prompt y fundamentación**

- [x] El prompt generado por `GroundedPromptBuilder` contiene el texto íntegro de los N chunks
      recuperados, cada uno con su `title` y su `chunkIndex`, en el mismo orden que la búsqueda.
- [x] El bloque de sistema incluye las tres reglas: responder solo con el contexto, admitir
      explícitamente cuando el contexto no alcanza, y responder en el idioma de la pregunta.

**Cliente LLM**

- [x] `OllamaLlmClient` con `HttpMessageHandler` mockeado devuelve el texto de una respuesta OK.
- [x] Un `500` HTTP del proveedor devuelve `Result.Failure("LlmUnavailable")` sin lanzar excepción.
- [x] Un handler que tarda más que `Rag:TimeoutSeconds` devuelve `Result.Failure("LlmTimeout")`
      sin lanzar excepción.

**Pipeline y orquestador**

- [x] Cuando ningún chunk supera `MinScore`, el `ILlmClient` **no recibe ninguna llamada** y el
      resultado es `FoundAnswer: false`, `Text: null`, `Citations: []`.
- [x] Con chunks por encima del umbral, `Answer.Citations` tiene exactamente tantos elementos
      como chunks devolvió la búsqueda.
- [x] Un fallo del `IEmbeddingGenerator` al embeber la pregunta se propaga como
      `Result.Failure("EmbeddingRequestFailed")` y no llega a consultar Qdrant.
- [x] `QueryController` depende de `KnowledgeOrchestrator` y **no** de `RagPipeline`.
- [x] `Answer.Model` refleja el valor de `Rag:Model` configurado, también en el camino sin resultados.

**Endpoint**

- [x] `POST /api/query` con índice poblado y pregunta pertinente devuelve `200` con
      `foundAnswer: true`, `answer` no vacío y `citations` con al menos un elemento.
- [x] Cada citación trae `title`, `sourceId`, `sourceType`, `chunkIndex` y `score`, y **no** trae
      `text` cuando `includeChunks` se omite o es `false`.
- [x] La misma consulta con `includeChunks: true` devuelve las citaciones **con** su `text`.
- [x] `POST /api/query` sin coincidencias por encima del umbral devuelve `200` con
      `foundAnswer: false`, `answer: null` y `citations: []`.
- [x] `POST /api/query` con `question` vacía, en blanco o ausente devuelve `400`.
- [x] Con un `ILlmClient` que falla con `LlmUnavailable`, la respuesta es `503` con
      `{ "error": "LlmUnavailable" }`.
- [x] Con un `ILlmClient` que agota el timeout, la respuesta es `504` con
      `{ "error": "LlmTimeout" }`.
- [x] Con un `IVectorStore` que falla, la respuesta es `503` con `{ "error": "VectorSearchFailed" }`.
- [x] `POST /api/ingest` y `GET /api/ingest/stats` siguen comportándose igual que en SPEC 02
      (la suite de integración de ingesta pasa sin cambios funcionales).

**Documentación**

- [x] `README.md` documenta `POST /api/query` con sus respuestas `200`/`400`/`503`/`504`, la
      sección `Rag` de configuración, y la tabla **modelo de desarrollo (CPU, `llama3.2:3b`) vs.
      modelo objetivo (GPU, `qwen2.5:7b`)** con la razón de latencia.
- [x] `CLAUDE.md` refleja SPEC 03 como implementado, los tipos nuevos por capa, la sección `Rag`
      y el aviso de que `SearchAsync` amplía el contrato de SPEC 02.

**Verificación manual (requiere AppHost con Qdrant y Ollama reales)**

- [ ] El dashboard de Aspire muestra los dos recursos de modelo (`embedding` y `chat`) en `Running`.
- [ ] Tras ingerir un documento conocido, una pregunta cuya respuesta está en él devuelve
      `foundAnswer: true` y cita ese documento en `citations`.
- [ ] Una pregunta sobre un tema **ausente** del corpus devuelve `foundAnswer: false` en lugar
      de una respuesta inventada.
- [ ] Una consulta genera en el dashboard una traza del `ActivitySource` `AiKnowledgeAssistant.Rag`
      con los spans `query.embed`, `query.search` y `query.generate`.
- [ ] Con `llama3.2:3b` en CPU, una consulta completa termina **por debajo del timeout de 60s**.

**Fuera de criterio: latencia del PRD**

El objetivo de **<5s del PRD no se verifica en este spec**. Es inalcanzable en CPU con los
modelos elegidos (ver cabecera y riesgos) y queda pendiente de una verificación en hardware con
GPU. Lo que sí se verifica aquí es que la consulta termina dentro del timeout configurado.

## Decisions

**Alcance y arquitectura**

- **Sí:** crear el `KnowledgeOrchestrator` ya en este spec, con las costuras de cache (SPEC 04) y
  persistencia (SPEC 05) declaradas pero vacías. Es la pieza que el PRD §7 pone en el centro, y
  el controlador queda acoplado a ella desde el principio. **No:** que el controlador llame
  directamente al pipeline RAG y el orquestador nazca en SPEC 04 — descartado porque obligaría a
  reescribir controlador y tests dos specs seguidos.
- **Sí:** el orquestador es el **único** punto de entrada del caso de uso; `RagPipeline` es un
  detalle interno que el controlador no conoce. Así SPEC 04 inserta el cache sin tocar la API.
- **Sí:** `ILlmClient` como abstracción propia en `Application`, con `OllamaLlmClient` en
  `Infrastructure` registrado por keyed service `"ollama"`, replicando exactamente el patrón de
  `IEmbeddingGenerator` de SPEC 02. Un solo mecanismo de pluggabilidad en el repo.
  **No:** usar el SDK de Ollama directamente desde `Application` — rompería la capa.
- **Sí:** OpenAI queda pluggable y sin implementar, igual que en SPEC 01 y SPEC 02.
- **Sí:** `Result<T>` para todo el flujo de negocio (LLM caído, timeout, búsqueda fallida).
  **No:** excepciones para flujo esperado — lo prohíbe el skill `dotnet-backend-patterns`.

**Retrieval**

- **Sí:** filtrar por `MinScore` además de `TopK`. Qdrant siempre devuelve K resultados aunque no
  tengan relación con la pregunta; sin umbral, FR-003 se degrada porque el LLM recibe contexto
  ruidoso y acaba rellenando huecos con conocimiento propio. **No:** top-K sin filtrar —
  descartado por ese motivo exacto.
- **Sí:** `MinScore: 0.5` en coseno como punto de partida **no validado**, ajustable por
  configuración. Se espera afinarlo tras las primeras consultas reales.
- **Sí:** `TopK` y `MinScore` **fijos por configuración**, sin override desde el request. Menos
  superficie de API y, sobre todo, el cache semántico de SPEC 04 no tendrá que versionar sus
  entradas por combinación de parámetros. **No:** `{"question": "...", "topK": 10}` — descartado
  por esa razón.
- **Sí:** búsqueda **general sobre todo el índice**, sin filtro por `sourceType` desde el request.
  El payload ya tiene `sourceType` indexable, así que añadirlo más adelante es barato.
- **Sí:** `SearchAsync` devuelve `Result<T>`, rompiendo la simetría con el resto de `IVectorStore`
  (que deja subir excepciones). Qdrant caído durante una consulta es un fallo **esperado** que
  debe salir como `503`, no como `500` opaco. **No:** mantener la simetría y propagar la
  excepción — descartado tras valorarlo explícitamente.
- **Sí:** `RetrievedChunk` como tipo distinto de `DocumentChunk`. El payload de Qdrant no guarda
  `TokenCount` y sí aporta `Score`. **No:** reutilizar `DocumentChunk` — obligaría a inventar
  valores para campos que la búsqueda no conoce.
- **No:** reranking, búsqueda híbrida (vectorial + BM25) y reescritura de la pregunta — las tres
  mejoran calidad de forma medible, pero ninguna es necesaria para el primer hito y las tres
  requieren un arnés de evaluación para saber si ayudan. Quedan identificadas como spec propio.

**Generación y modelo**

- **Sí:** `llama3.2:3b` como valor por defecto en `appsettings.json` y `qwen2.5:7b` documentado
  como modelo objetivo en despliegue con GPU. El modelo es `IOptions`, así que cambiar entre
  ambos no toca código. Decisión tomada tras comprobar que la máquina de desarrollo tiene GPU
  integrada Intel (no acelerable por Ollama, que requiere CUDA o ROCm): con el 7b en CPU, una
  consulta RAG ronda 1–2 minutos entre lectura del prompt y generación, de modo que el timeout de
  60s saltaría en consultas normales. **No:** `qwen2.5:7b` por defecto con timeout de 180s —
  descartado porque haría cada prueba manual insoportablemente lenta. **No:** ocultar el 7b y
  quedarse solo con el 3b — descartado porque el modelo objetivo debe quedar escrito.
- **Sí:** el objetivo de `<5s` del PRD queda **explícitamente fuera de los criterios de
  aceptación**, con su justificación. Preferimos un spec que declara lo que no cumple a uno con
  un criterio aspiracional que nadie marca. Se retoma cuando haya hardware con GPU.
- **Sí:** `Temperature: 0.2`. FR-003 pide respuestas fundamentadas, no creativas.
- **Sí:** respuesta **JSON completa**, sin streaming. **No:** SSE token a token — descartado por
  complicar el contrato, los tests de integración y, sobre todo, el cache de SPEC 04, que
  necesita la respuesta entera para almacenarla. El PRD no pide streaming.
- **Sí:** responder **en el idioma de la pregunta**, por instrucción de prompt. **No:** forzar
  siempre español — descartado por producir traducciones pobres de documentación técnica en inglés.
- **Sí:** cada consulta es independiente, sin historial ni multi-turno. Lo excluye el propio PRD.

**Contrato HTTP y errores**

- **Sí:** sin resultados por encima del umbral → `200` con `answer: null` y `foundAnswer: false`,
  **sin llamar al LLM**. Contrato uniforme, el cliente distingue "no sé" de "aquí tienes" sin
  parsear texto, y se ahorra la llamada más cara del pipeline. **No:** que el LLM redacte un "no
  encontré información" — descartado por gastar 30s en decir que no. **No:** `404` — descartado
  porque la consulta se procesó correctamente; no hay recurso ausente.
- **Sí:** citaciones con metadatos por defecto y `text` solo bajo `includeChunks: true`. Mantiene
  las respuestas ligeras y no expone el contenido íntegro de documentos internos por una API que
  **hoy no tiene autenticación**, sin renunciar a poder depurar qué contexto recibió el modelo.
  **No:** texto siempre incluido — descartado por peso y exposición. **No:** solo metadatos sin
  flag — descartado porque depurar exigiría consultar Qdrant a mano.
- **Sí:** `503 LlmUnavailable` para Ollama caído o modelo aún descargando. Es exactamente el
  escenario de los primeros minutos tras levantar el AppHost, ya conocido de SPEC 02 con los
  embeddings. **No:** `500` genérico — descartado por opaco para el cliente.
- **Sí:** `504 LlmTimeout` con `Rag:TimeoutSeconds: 60`, en vez de dejar morir la conexión.
- **Sí:** `includeChunks` tratado como decisión de **presentación**: el pipeline siempre devuelve
  el texto y el controlador decide si lo serializa. Mantiene una sola ruta de ejecución.
- **Sí:** endpoint de consulta **sin autenticación**, consistente con el resto del MVP. Riesgo
  asumido y registrado abajo.

**Tests y verificación**

- **Sí:** misma estrategia que SPEC 02 — integración con `WebApplicationFactory` sustituyendo
  `IEmbeddingGenerator`, `IVectorStore` e `ILlmClient` por dobles en memoria, de forma que
  `dotnet test` siga corriendo **sin Docker**. **No:** Testcontainers con Qdrant y Ollama reales —
  descartado por volver los tests lentos y dependientes del entorno; la verificación real queda
  como criterio manual.
- **Sí:** un test que verifica que el LLM **no recibe llamada alguna** cuando no hay chunks. Es la
  única forma de que "no llamamos al LLM sin contexto" sea un hecho comprobable y no una intención.
- **No:** arnés de evaluación de calidad del RAG (groundedness, precisión, recall) — descartado
  por ser un proyecto en sí mismo. Sin él, la calidad de las respuestas se juzga manualmente en
  este hito.

## Risks

| Riesgo | Mitigación |
| --- | --- |
| **`MinScore: 0.5` no está validado contra el corpus real.** Los modelos tipo `nomic-embed-text` dan scores altos incluso entre textos sin relación (dos fragmentos ajenos pueden puntuar 0.5–0.6 solo por compartir idioma y registro), así que el umbral puede resultar demasiado permisivo y dejar pasar ruido al prompt. | Valor configurable en `Rag:MinScore`, ajustable sin tocar código. El flag `includeChunks: true` existe precisamente para inspeccionar los scores reales: la primera sesión de pruebas lanza una consulta con respuesta clara en el corpus y otra deliberadamente ajena, y con esos números se fija el valor definitivo. |
| **Latencia muy por encima del objetivo del PRD en la máquina de desarrollo.** GPU integrada Intel, no acelerable por Ollama (requiere CUDA o ROCm): la generación va por CPU y el `<5s` es inalcanzable. | Decisión consciente y documentada: `llama3.2:3b` por defecto (10–20s por consulta) y `qwen2.5:7b` como modelo objetivo en despliegue con GPU. El `<5s` queda explícitamente fuera de los criterios de aceptación en vez de figurar como criterio que nadie puede marcar. |
| **Ollama descargando el modelo de chat hace fallar toda consulta durante los primeros minutos** tras levantar el AppHost, igual que ya ocurre con los embeddings en SPEC 02. | El modelo se declara como recurso Aspire propio, visible en el dashboard con su estado. El fallo sale como `503 LlmUnavailable` —explícito y reintentable— y no como `500` opaco. Los tests no dependen de ello. |
| **`llama3.2:3b` es un modelo pequeño: puede ignorar la instrucción de responder solo con el contexto** y completar huecos con conocimiento propio, que es exactamente lo que FR-003 prohíbe. | El prompt es explícito en las tres reglas y `Temperature: 0.2` reduce la deriva. La verificación manual incluye un criterio específico: una pregunta sobre un tema ausente del corpus debe devolver `foundAnswer: false`, no una respuesta inventada. Si el 3b falla ahí de forma sistemática, es señal de subir de modelo, no de relajar el criterio. |
| **Sin reranking ni búsqueda híbrida, la calidad del retrieval depende por completo de `TopK` y `MinScore`.** Preguntas cuya respuesta se apoya en terminología exacta (códigos, nombres de campo) pueden fallar, porque la búsqueda vectorial pura no prioriza coincidencia léxica. | Limitación aceptada para el primer hito y registrada como spec propio. `TopK: 5` da margen suficiente para que el chunk correcto aparezca aunque no sea el primero. |
| **Sin arnés de evaluación, la calidad de las respuestas solo se juzga manualmente.** Una regresión al cambiar prompt, modelo o umbral pasa desapercibida. | Aceptado en este hito. Los tests automatizados cubren el **comportamiento** del pipeline (no llamar al LLM sin contexto, propagar fallos, forma de las citaciones), no la calidad del texto generado. El arnés queda identificado como spec propio. |
| **El endpoint de consulta no tiene autenticación**: cualquiera en la red puede preguntar sobre documentación interna y, con `includeChunks: true`, extraer el texto íntegro de los chunks indexados. | Riesgo aceptado para el MVP en red interna, consistente con SPEC 01 y 02. Que `includeChunks` sea opt-in limita la exposición por defecto. **No desplegar la Api a una red no confiable antes del spec de autenticación** — el aviso ya presente en `README.md` por la ingesta aplica igual aquí. |
| **`SearchAsync` amplía `IVectorStore`, un contrato de SPEC 02**, y rompe la compilación de los dobles en memoria hasta actualizarlos. | El paso 3 del plan hace ambas cosas a la vez, deliberadamente pronto, para no arrastrar una solución que no compila. Los tests de ingesta existentes deben seguir pasando sin cambios funcionales, y hay un criterio de aceptación que lo exige. |
| **Una consulta no deja rastro persistente** (FR-004 y FR-007 sin cubrir): si una respuesta sale mal, no hay registro para analizarla más allá de las trazas de OpenTelemetry, que son efímeras. | Consecuencia asumida del orden de specs; la persistencia es SPEC 05. La telemetría de SPEC 03 registra pregunta, número de chunks recuperados, score máximo y modelo, lo que permite diagnóstico en caliente desde el dashboard de Aspire. |

## What is **not** in this spec

- Cache semántico (FR-005) → SPEC 04. El orquestador deja la costura declarada.
- Persistencia de preguntas/respuestas, feedback y auditoría (FR-004/006/007) → SPEC 05.
- Streaming SSE, conversación multi-turno e historial.
- Reranking, búsqueda híbrida y reescritura/expansión de la pregunta.
- Filtrado del retrieval por `sourceType` desde el request y override de `TopK`/`MinScore`.
- Marcadores de citación en línea dentro del texto de la respuesta.
- OpenAI como proveedor de LLM.
- Autenticación del endpoint de consulta.
- Arnés de evaluación de calidad del RAG y tuning con métricas.
- Conector de Confluence.
- Testcontainers / Qdrant y Ollama reales en la suite de tests.

Cada uno de estos, cuando llegue, va en su propio spec.
