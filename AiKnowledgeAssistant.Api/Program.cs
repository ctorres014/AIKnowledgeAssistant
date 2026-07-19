var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery, HTTP resilience.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Aspire default endpoints: /health and /alive (health checks).
app.MapDefaultEndpoints();

// MVC controllers (e.g. POST /api/query).
app.MapControllers();

app.Run();

// Exposed so WebApplicationFactory<Program> can bootstrap the app in integration tests.
public partial class Program;
