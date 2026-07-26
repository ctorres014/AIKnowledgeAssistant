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

// API project with references to the infrastructure resources (connection strings /
// endpoints are injected by Aspire via WithReference).
builder.AddProject<Projects.AiKnowledgeAssistant_Api>("api")
    .WithReference(postgres)
    .WithReference(qdrant)
    .WithReference(ollama)
    .WithReference(embedding);

builder.Build().Run();
