var builder = DistributedApplication.CreateBuilder(args);

// Infrastructure resources. SPEC 01 only wires them; no code invokes them yet.
var postgres = builder.AddPostgres("postgres")
    .AddDatabase("knowledgedb");

var qdrant = builder.AddQdrant("qdrant");

var ollama = builder.AddOllama("ollama");

// Embedding model used by the ingestion pipeline (SPEC 02). It appears as its own resource in the
// dashboard: the first run downloads ~270 MB, so it stays "Starting" for a few minutes while the
// rest of the app is already up. Ingest calls made during that window fail per document with
// EmbeddingRequestFailed and can simply be retried.
var embedding = ollama.AddModel("embedding", "nomic-embed-text");

// Generation model used by the RAG pipeline (SPEC 03), as its own resource for the same reason:
// its first pull is visible in the dashboard, and queries made while it is still "Starting" fail
// explicitly with 503 LlmUnavailable instead of an opaque error. llama3.2:3b is the development
// model — small enough to answer in 10-20s on CPU; qwen2.5:7b is the target on a GPU host.
// Keep the tag in step with Rag:Model in appsettings.json.
var chat = ollama.AddModel("chat", "llama3.2:3b");

// API project with references to the infrastructure resources (connection strings /
// endpoints are injected by Aspire via WithReference).
builder.AddProject<Projects.AiKnowledgeAssistant_Api>("api")
    .WithReference(postgres)
    .WithReference(qdrant)
    .WithReference(ollama)
    .WithReference(embedding)
    .WithReference(chat);

builder.Build().Run();
