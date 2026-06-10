using AgentOrion.Api.Endpoints;
using AgentOrion.Core.Configuration;
using AgentOrion.Core.Operations;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Copilot;
using AgentOrion.Infrastructure.Operations;
using AgentOrion.Infrastructure.Persistence;
using AgentOrion.Infrastructure.Persistence.Repositories;
using AgentOrion.Infrastructure.Tools;
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
builder.Services.AddSingleton<IAgentOrionDbConnectionFactory>(sp => sp.GetRequiredService<TursoContext>());
builder.Services.AddSingleton<IDatabaseHealthService, DatabaseHealthService>();

// Repositories
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<IShipmentRepository, ShipmentRepository>();
builder.Services.AddScoped<IConversationMemoryRepository, ConversationMemoryRepository>();
builder.Services.AddScoped<IAgentAuditRepository, AgentAuditRepository>();
builder.Services.AddScoped<IShipmentEventRepository, ShipmentEventRepository>();
builder.Services.AddScoped<ISimulatedEmailRepository, SimulatedEmailRepository>();

if (string.Equals(options.Operations.AwbApi.Mode, "http", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(options.Operations.AwbApi.BaseUrl))
    {
        throw new InvalidOperationException("AgentOrion:Operations:AwbApi:BaseUrl is required when AwbApi Mode is 'http'.");
    }

    builder.Services.AddHttpClient<IAwbReservationGateway, HttpAwbReservationGateway>((sp, client) =>
    {
        var cfg = sp.GetRequiredService<IOptions<AgentOrionOptions>>().Value.Operations.AwbApi;
        client.BaseAddress = new Uri(cfg.BaseUrl!, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 1, 300));
    });
}
else
{
    builder.Services.AddScoped<IAwbReservationGateway, FakeAwbReservationGateway>();
}

builder.Services.AddScoped<ToolCatalog>();
builder.Services.AddSingleton<ConversationMemoryService>();
builder.Services.AddSingleton<ConversationTurnCoordinator>();
builder.Services.AddScoped<IWorkflowInputExtractor<AwbReservationDraft>, AwbReservationDraftExtractor>();
builder.Services.AddScoped<IWorkflowValidator<AwbReservationDraft>, AwbReservationDraftValidator>();
builder.Services.AddScoped<IWorkflowResponseComposer<AwbReservationDraft>, AwbReservationResponseComposer>();
builder.Services.AddScoped<IAgentWorkflow, AwbReservationWorkflow>();
builder.Services.AddScoped<WorkflowCoordinator>();
builder.Services.AddSingleton(sp => new AgentCatalogProvider(
    sp.GetRequiredService<IOptions<AgentOrionOptions>>(),
    builder.Environment.ContentRootPath));
builder.Services.AddSingleton(sp => new SkillRegistry(
    sp.GetRequiredService<IOptions<AgentOrionOptions>>(),
    builder.Environment.ContentRootPath));
builder.Services.AddSingleton<AgentSessionManager>();
builder.Services.AddSingleton<AgentPermissionPolicy>();
builder.Services.AddSingleton<AgentRequestRouter>();
builder.Services.AddScoped<ChatTurnService>();

// Copilot Agent Factory (Singleton para reusar el CopilotClient)
builder.Services.AddSingleton(sp =>
    new AgentFactory(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOrionOptions>>(),
        sp.GetRequiredService<AgentRequestRouter>(),
        sp.GetRequiredService<SkillRegistry>(),
        sp.GetRequiredService<AgentSessionManager>()));

// CORS para el frontend React en Vite (puerto 5173 por defecto)
builder.Services.AddCors(c => c.AddPolicy("Frontend", p =>
    p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
     .AllowAnyMethod()
     .AllowAnyHeader()));

var app = builder.Build();
var catalog = app.Services.GetRequiredService<AgentCatalogProvider>().Catalog;
app.Services.GetRequiredService<SkillRegistry>().ValidateCatalog(catalog);
ToolCatalog.ValidateCatalog(catalog);
app.UseCors("Frontend");

// Health check
app.MapGet("/health", () => new { status = "ok", agent = "AgentOrion", version = "0.1.0" });

app.MapGet("/api/db/health", async (IDatabaseHealthService health, CancellationToken ct) =>
{
    var status = await health.CheckAsync(ct);
    return status.CanConnect ? Results.Ok(status) : Results.Problem(status.Error, statusCode: 503);
});

app.MapGet("/api/runtime", (
    IOptions<AgentOrionOptions> options,
    AgentCatalogProvider catalogProvider,
    SkillRegistry skillRegistry) =>
{
    var cfg = options.Value;
    return Results.Ok(new
    {
        agentName = "Code Name Orion",
        model = cfg.Copilot.Model,
        defaultMode = ChatModes.Memory,
        supportedModes = new[] { ChatModes.Fast, ChatModes.Memory },
        providerConfigured = !string.IsNullOrWhiteSpace(cfg.Copilot.Provider.ApiKey),
        skillCount = skillRegistry.SkillNames.Count,
        routeCount = catalogProvider.Catalog.Routes.Count
    });
});

// Chat SSE endpoint
app.MapChatEndpoints();

// Endpoints de negocio (CRUD directo)
app.MapGet("/api/customers", async (int? limit, ICustomerRepository repo) =>
    Results.Ok(limit.HasValue ? await repo.GetRecentAsync(limit.Value) : await repo.GetAllAsync()));

app.MapGet("/api/customers/{id:int}", async (int id, ICustomerRepository repo) =>
    await repo.GetByIdAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

app.MapPost("/api/customers", async (AgentOrion.Core.Models.Customer customer, ICustomerRepository repo) =>
{
    var id = await repo.CreateAsync(customer);
    return Results.Created($"/api/customers/{id}", new { id });
});

app.MapGet("/api/shipments", async (int? limit, IShipmentRepository repo) =>
    Results.Ok(limit.HasValue ? await repo.GetRecentAsync(limit.Value) : await repo.GetAllAsync()));

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
