var builder = DistributedApplication.CreateBuilder(args);

// Infrastructure resources. SPEC 01 only wires them; no code invokes them yet.
var postgres = builder.AddPostgres("postgres")
    .AddDatabase("knowledgedb");

var qdrant = builder.AddQdrant("qdrant");

var ollama = builder.AddOllama("ollama");

// API project with references to the infrastructure resources (connection strings /
// endpoints are injected by Aspire via WithReference).
builder.AddProject<Projects.AiKnowledgeAssistant_Api>("api")
    .WithReference(postgres)
    .WithReference(qdrant)
    .WithReference(ollama);

builder.Build().Run();
