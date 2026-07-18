# Product Requirements Document (PRD)

# AI Knowledge Assistant

## 1. Purpose
Construir una plataforma de consulta de conocimiento corporativo basada en IA que permita responder preguntas utilizando información interna mediante RAG.

## 2. Problem
La información está distribuida en múltiples fuentes (Confluence, PDF, Markdown, TXT), dificultando su búsqueda y reutilización.

## 3. Goals
- Consultas en lenguaje natural.
- Respuestas fundamentadas en documentación.
- Aprendizaje continuo mediante almacenamiento de preguntas.
- Reducción del tiempo de búsqueda.

## 4. Scope (MVP)
### Incluye
- API REST.
- Ingesta de Confluence, PDF, TXT y Markdown.
- Embeddings.
- Qdrant.
- Pipeline RAG.
- Persistencia de preguntas/respuestas.
- Semantic Cache.
- Telemetría con Aspire.

### No incluye
- Chat multi-turno.
- Agentes autónomos.
- Edición automática de documentación.

## 5. Functional Requirements
- FR-001 Consulta en lenguaje natural.
- FR-002 Recuperación desde múltiples fuentes.
- FR-003 Respuestas únicamente con contexto recuperado.
- FR-004 Persistencia de preguntas.
- FR-005 Reutilización mediante similitud semántica.
- FR-006 Feedback del usuario.
- FR-007 Auditoría completa.

## 6. Non Functional Requirements
- Cache <1s.
- RAG <5s.
- Escalabilidad horizontal.
- Observabilidad.
- Seguridad basada en identidad corporativa.

## 7. Architecture
User -> API -> Knowledge Orchestrator -> Semantic Cache -> RAG -> Qdrant -> LLM -> Persistencia -> Response

## 8. Technology
- C# / .NET Aspire
- PostgreSQL
- Qdrant
- OpenTelemetry
- OpenAI/Ollama
- RAG

## 9. Success Metrics
- >70% preguntas respondidas.
- >40% cache hit.
- Feedback positivo >80%.
