using AiKnowledgeAssistant.Infrastructure.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, HTTP resilience.
builder.AddServiceDefaults();

// Ingestion pipeline (SPEC 02): document sources, chunker, Ollama embeddings, Qdrant vector store.
builder.AddIngestion();

// Query path (SPEC 03): Ollama chat client, grounded prompt, RAG pipeline, Knowledge Orchestrator.
// After AddIngestion, whose embedding generator and vector store the pipeline reuses.
builder.AddRag();

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // OpenAPI document at /openapi/v1.json, browsable through Swagger UI at /swagger.
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "AI Knowledge Assistant v1");
        options.DocumentTitle = "AI Knowledge Assistant API";
    });
}

app.UseHttpsRedirection();

// Aspire default endpoints: /health and /alive (health checks).
app.MapDefaultEndpoints();

// MVC controllers (e.g. POST /api/query).
app.MapControllers();

app.Run();

// Exposed so WebApplicationFactory<Program> can bootstrap the app in integration tests.
public partial class Program;
