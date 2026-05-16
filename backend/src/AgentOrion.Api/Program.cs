using AgentOrion.Api.Endpoints;
using AgentOrion.Core.Configuration;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Copilot;
using AgentOrion.Infrastructure.Persistence;
using AgentOrion.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Options
builder.Services.Configure<AgentOrionOptions>(
    builder.Configuration.GetSection("AgentOrion"));

// Singleton TursoContext (SQLite local in-process)
var options = builder.Configuration.GetSection("AgentOrion").Get<AgentOrionOptions>()
              ?? new AgentOrionOptions();
var dbPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, options.DbPath));
builder.Services.AddSingleton(new TursoContext(dbPath));

// Repositories
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IShipmentRepository, ShipmentRepository>();
builder.Services.AddScoped<IConversationMemoryRepository, ConversationMemoryRepository>();
builder.Services.AddSingleton<ConversationMemoryService>();
builder.Services.AddSingleton<ConversationTurnCoordinator>();
builder.Services.AddSingleton(sp => new AgentCatalogProvider(
    sp.GetRequiredService<IOptions<AgentOrionOptions>>(),
    builder.Environment.ContentRootPath));
builder.Services.AddSingleton<AgentRequestRouter>();

// Copilot Agent Factory (Singleton para reusar el CopilotClient)
builder.Services.AddSingleton(sp =>
    new AgentFactory(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOrionOptions>>(),
        sp.GetRequiredService<AgentRequestRouter>(),
        builder.Environment.ContentRootPath));

// CORS para el frontend React en Vite (puerto 5173 por defecto)
builder.Services.AddCors(c => c.AddPolicy("Frontend", p =>
    p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
     .AllowAnyMethod()
     .AllowAnyHeader()));

var app = builder.Build();
app.UseCors("Frontend");

// Health check
app.MapGet("/health", () => new { status = "ok", agent = "AgentOrion", version = "0.1.0" });

app.MapGet("/api/runtime", (IOptions<AgentOrionOptions> options) =>
{
    var cfg = options.Value;
    return Results.Ok(new
    {
        agentName = "Code Name Orion",
        model = cfg.Copilot.Model,
        defaultMode = ChatModes.Memory,
        supportedModes = new[] { ChatModes.Fast, ChatModes.Memory }
    });
});

// Chat SSE endpoint
app.MapChatEndpoints();

// Endpoints de negocio (CRUD directo)
app.MapGet("/api/customers", async (ICustomerRepository repo) =>
    Results.Ok(await repo.GetAllAsync()));

app.MapGet("/api/customers/{id:int}", async (int id, ICustomerRepository repo) =>
    await repo.GetByIdAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

app.MapPost("/api/customers", async (AgentOrion.Core.Models.Customer customer, ICustomerRepository repo) =>
{
    var id = await repo.CreateAsync(customer);
    return Results.Created($"/api/customers/{id}", new { id });
});

app.MapGet("/api/shipments", async (IShipmentRepository repo) =>
    Results.Ok(await repo.GetAllAsync()));

app.MapGet("/api/shipments/{awb}", async (string awb, IShipmentRepository repo) =>
    await repo.GetByAwbAsync(awb) is { } s ? Results.Ok(s) : Results.NotFound());

app.MapGet("/api/debug/byok", async (IOptions<AgentOrionOptions> options) =>
{
    var cfg = options.Value;

    await using var client = new GitHub.Copilot.SDK.CopilotClient(new GitHub.Copilot.SDK.CopilotClientOptions
    {
        UseLoggedInUser = false,
        GitHubToken = null,
        LogLevel = "debug"
    });

    await using var session = await client.CreateSessionAsync(new GitHub.Copilot.SDK.SessionConfig
    {
        Model = cfg.Copilot.Model,
        Provider = new GitHub.Copilot.SDK.ProviderConfig
        {
            Type = cfg.Copilot.Provider.Type,
            BaseUrl = cfg.Copilot.Provider.BaseUrl,
            ApiKey = cfg.Copilot.Provider.ApiKey,
            WireApi = cfg.Copilot.Provider.WireApi
        },
        OnPermissionRequest = GitHub.Copilot.SDK.PermissionHandler.ApproveAll
    });

    var response = await session.SendAndWaitAsync(new GitHub.Copilot.SDK.MessageOptions
    {
        Prompt = "Responde solo con OK"
    });

    return Results.Ok(new { content = response?.Data.Content });
});

// Static files (frontend build) + SPA fallback para producción/Docker
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

if (!app.Environment.IsEnvironment("Testing"))
{
    try
    {
        await app.Services.GetRequiredService<AgentFactory>().WarmUpAsync();
    }
    catch
    {
        // Best effort: el primer turno puede tardar más si el warm-up falla.
    }
}

app.Run();

public partial class Program;
