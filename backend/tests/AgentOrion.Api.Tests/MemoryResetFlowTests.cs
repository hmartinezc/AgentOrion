using System.Net.Http.Json;
using AgentOrion.Api.Endpoints;
using AgentOrion.Core.Configuration;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Copilot;
using AgentOrion.Infrastructure.Persistence;
using AgentOrion.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentOrion.Api.Tests;

public class MemoryResetFlowTests
{
    [Fact]
    public async Task ResetEndpoint_ClearsPersistedConversationMemory()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();

        var sessionId = "memory-reset-test";

        await SeedMemoryAsync(app.Services, sessionId);

        using (var scope = app.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IConversationMemoryRepository>();
            var existing = await repository.GetAsync(sessionId);

            Assert.NotNull(existing);
            Assert.Equal("JAF Flower", existing!.Customer.FullName);
            Assert.Equal("AWB-FLO-TEST-01", existing.Shipment.AwbNumber);
        }

        var response = await client.PostAsJsonAsync("/api/chat/reset", new ResetChatRequest(sessionId));
        response.EnsureSuccessStatusCode();

        using (var scope = app.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IConversationMemoryRepository>();
            var afterReset = await repository.GetAsync(sessionId);
            Assert.Null(afterReset);
        }
    }

    [Fact]
    public void ConversationMemoryService_BuildsAppendixFromPromptAndToolResults()
    {
        var service = new ConversationMemoryService();
        var memory = service.Create("memory-build-test");

        service.ApplyPrompt(memory, "Se llama JAF Flower, email jaf@gmail.com, telefono +1523698744, direccion 2811 NW 74th Ave Miami FL, documento 142555522.");
        service.ApplyPrompt(memory, "Ahora crea una reserva AWB para flores rosas, 25 kg, origen BOG, destino MIA, fecha 2026-05-20 para ese cliente.");
        service.ApplyToolResult(memory, "register_customer", "{\"customerId\":2,\"fullName\":\"JAF Flower\",\"companyName\":\"JAF Flower\"}");
        service.ApplyToolResult(memory, "create_awb", "{\"awbNumber\":\"AWB-FLO-TEST-01\",\"status\":\"solicitado\",\"flightDate\":\"2026-05-20\",\"temperatureRequiredC\":2}");

        var appendix = service.BuildContextAppendix(memory);

        Assert.Contains("MEMORIA ESTRUCTURADA DE LA CONVERSACION", appendix);
        Assert.Contains("nombre=JAF Flower", appendix);
        Assert.Contains("email=jaf@gmail.com", appendix);
        Assert.Contains("direccion=2811 NW 74th Ave Miami FL", appendix);
        Assert.Contains("awb=AWB-FLO-TEST-01", appendix);
        Assert.Contains("origen=BOG", appendix);
        Assert.Contains("destino=MIA", appendix);
    }

    [Fact]
    public async Task DeterministicTurnHandler_HandlesSimpleGreetingWithoutLlmSession()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();

        var result = await DeterministicTurnHandler.TryHandleAsync(
            "hola",
            null,
            scope.ServiceProvider.GetRequiredService<ConversationMemoryService>(),
            scope.ServiceProvider.GetRequiredService<ICustomerRepository>(),
            scope.ServiceProvider.GetRequiredService<IShipmentRepository>());

        Assert.NotNull(result);
        Assert.Equal("operations-general", result!.RouteName);
        Assert.Contains("soy Orion", result.Content);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task DeterministicTurnHandler_AsksForReservationDataWithoutLlmSession()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();

        var result = await DeterministicTurnHandler.TryHandleAsync(
            "crear reserva AWB",
            null,
            scope.ServiceProvider.GetRequiredService<ConversationMemoryService>(),
            scope.ServiceProvider.GetRequiredService<ICustomerRepository>(),
            scope.ServiceProvider.GetRequiredService<IShipmentRepository>());

        Assert.NotNull(result);
        Assert.Equal("awb-dispatch", result!.RouteName);
        Assert.Contains("Para crear una reserva AWB necesito", result.Content);
        Assert.DoesNotContain("search_customer", result.Tools);
    }

    [Fact]
    public async Task DeterministicTurnHandler_FindsCustomerForIncompleteReservationWithoutLlmSession()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();
        var customerRepository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var memoryService = scope.ServiceProvider.GetRequiredService<ConversationMemoryService>();
        var memory = memoryService.Create("deterministic-jaf-test");

        await customerRepository.CreateAsync(new AgentOrion.Core.Models.Customer
        {
            FullName = "JAF Flower",
            CompanyName = "JAF Flower",
            Email = "jaf@gmail.com"
        });

        var result = await DeterministicTurnHandler.TryHandleAsync(
            "quiero crear una nueva reserva para el cliente jaf",
            memory,
            memoryService,
            customerRepository,
            scope.ServiceProvider.GetRequiredService<IShipmentRepository>());

        Assert.NotNull(result);
        Assert.Equal("awb-dispatch", result!.RouteName);
        Assert.Contains("search_customer", result.Tools);
        Assert.Contains("Encontre al cliente JAF Flower", result.Content);
        Assert.Equal("JAF Flower", memory.Customer.FullName);
        Assert.Equal("awb_creation", memory.CurrentIntent);
    }

    [Fact]
    public async Task DeterministicTurnHandler_CreatesCompleteReservationWithoutLlmSession()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();
        var customerRepository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var memoryService = scope.ServiceProvider.GetRequiredService<ConversationMemoryService>();
        var memory = memoryService.Create("deterministic-awb-test");

        await customerRepository.CreateAsync(new AgentOrion.Core.Models.Customer
        {
            FullName = "JAF Flower",
            CompanyName = "JAF Flower",
            Email = "jaf@gmail.com"
        });

        var result = await DeterministicTurnHandler.TryHandleAsync(
            "reserva para el cliente jaf flores rosas 25 kg origen BOG destino MIA fecha 2026-05-20",
            memory,
            memoryService,
            customerRepository,
            scope.ServiceProvider.GetRequiredService<IShipmentRepository>());

        Assert.NotNull(result);
        Assert.Equal("awb-dispatch", result!.RouteName);
        Assert.Contains("search_customer", result.Tools);
        Assert.Contains("create_awb", result.Tools);
        Assert.Contains("Reserva AWB creada para JAF Flower", result.Content);
        Assert.Equal("JAF Flower", memory.Customer.FullName);
        Assert.Equal("flores", memory.Shipment.ProductType);
        Assert.Equal("BOG", memory.Shipment.OriginAirport);
        Assert.Equal("MIA", memory.Shipment.DestinationAirport);
        Assert.NotNull(memory.Shipment.AwbNumber);
    }

    [Fact]
    public async Task DeterministicTurnHandler_CreatesReservationFromCustomerMemoryAndSpanishDate()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();
        var customerRepository = scope.ServiceProvider.GetRequiredService<ICustomerRepository>();
        var shipmentRepository = scope.ServiceProvider.GetRequiredService<IShipmentRepository>();
        var memoryService = scope.ServiceProvider.GetRequiredService<ConversationMemoryService>();
        var memory = memoryService.Create("deterministic-pedro-crab-test");

        var customerId = await customerRepository.CreateAsync(new AgentOrion.Core.Models.Customer
        {
            FullName = "Pedro Valdair",
            CompanyName = "Mexican Can Inc",
            Email = "pedro.valdair@example.com"
        });
        memory.CurrentIntent = "awb_creation";
        memory.Customer.CustomerId = customerId;
        memory.Customer.FullName = "Pedro Valdair";

        var result = await DeterministicTurnHandler.TryHandleAsync(
            "Producto: Cangrejo (500 kg)\nRuta: BOG -> MIA\nFecha: 22 de mayo de 2026",
            memory,
            memoryService,
            customerRepository,
            shipmentRepository);

        Assert.NotNull(result);
        Assert.Equal("awb-dispatch", result!.RouteName);
        Assert.DoesNotContain("search_customer", result.Tools);
        Assert.Contains("create_awb", result.Tools);
        Assert.Contains("Reserva AWB creada para Pedro Valdair", result.Content);
        Assert.Contains("Cangrejo", result.Content);
        Assert.Contains("2026-05-22", result.Content);
        Assert.Equal("mariscos", memory.Shipment.ProductType);
        Assert.Equal("Cangrejo", memory.Shipment.ProductName);
        Assert.Equal(500, memory.Shipment.QuantityKg);
        Assert.Equal("BOG", memory.Shipment.OriginAirport);
        Assert.Equal("MIA", memory.Shipment.DestinationAirport);
        Assert.Equal(new DateTime(2026, 5, 22), memory.Shipment.FlightDate);
        Assert.NotNull(memory.Shipment.AwbNumber);

        var followUp = await DeterministicTurnHandler.TryHandleAsync(
            "si se creo? cual es el awb?",
            memory,
            memoryService,
            customerRepository,
            shipmentRepository);

        Assert.NotNull(followUp);
        Assert.Equal("awb-dispatch", followUp!.RouteName);
        Assert.Empty(followUp.Tools);
        Assert.Contains("Si, la reserva AWB esta creada", followUp.Content);
        Assert.Contains(memory.Shipment.AwbNumber, followUp.Content);
        Assert.Contains("Pedro Valdair", followUp.Content);
    }

    [Fact]
    public void AgentCatalog_LoadsRoutesFromJsonAndRoutesAwbRequests()
    {
        var catalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentOrion.Api", "agent-catalog.json"));
        var catalogProvider = new AgentCatalogProvider(
            Options.Create(new AgentOrionOptions { AgentCatalogPath = catalogPath }),
            Path.GetDirectoryName(catalogPath)!);
        var router = new AgentRequestRouter(catalogProvider);
        var profile = router.SelectProfile(
            "crear reserva AWB",
            new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("awb-dispatch", profile.RouteName);
        Assert.Equal("Despacho AWB", profile.DisplayName);
        Assert.Contains("awb-dispatch", profile.SkillNames);

        var mixedProfile = router.SelectProfile(
            "crear reserva AWB para flores",
            new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("mixed-operations", mixedProfile.RouteName);
        Assert.Contains("awb-dispatch", mixedProfile.SkillNames);
        Assert.Contains("cold-chain", mixedProfile.SkillNames);
    }

    private static async Task SeedMemoryAsync(IServiceProvider services, string sessionId)
    {
        using var scope = services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IConversationMemoryRepository>();

        await repository.UpsertAsync(new AgentOrion.Core.Models.ConversationMemoryState
        {
            SessionId = sessionId,
            LastRouteName = "mixed-operations",
            CurrentIntent = "awb_creation",
            Customer = new AgentOrion.Core.Models.ConversationCustomerMemory
            {
                CustomerId = 2,
                FullName = "JAF Flower",
                CompanyName = "JAF Flower",
                Email = "jaf@gmail.com",
                Phone = "+1523698744",
                Address = "2811 NW 74th Ave Miami FL",
                DocumentNumber = "142555522"
            },
            Shipment = new AgentOrion.Core.Models.ConversationShipmentMemory
            {
                AwbNumber = "AWB-FLO-TEST-01",
                ProductType = "flores",
                ProductName = "Rosas",
                QuantityKg = 25,
                OriginAirport = "BOG",
                DestinationAirport = "MIA",
                FlightDate = new DateTime(2026, 5, 20),
                TemperatureRequiredC = 2,
                Status = "solicitado"
            },
            UpdatedAt = DateTime.UtcNow
        });
    }

    private sealed class TestApplication : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"agentorion-tests-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentOrion:DbPath"] = _dbPath,
                    ["AgentOrion:SkillDirectories:0"] = "../AgentOrion.Skills",
                    ["AgentOrion:Copilot:Model"] = "test-model",
                    ["AgentOrion:Copilot:Provider:Type"] = "openai",
                    ["AgentOrion:Copilot:Provider:BaseUrl"] = "https://example.test/v1",
                    ["AgentOrion:Copilot:Provider:ApiKey"] = "test-key",
                    ["AgentOrion:Copilot:Provider:WireApi"] = "completions"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TursoContext>();
                services.RemoveAll<IConversationMemoryRepository>();
                services.RemoveAll<AgentCatalogProvider>();
                services.RemoveAll<AgentRequestRouter>();

                services.AddSingleton(new TursoContext(_dbPath));
                services.AddScoped<IConversationMemoryRepository, ConversationMemoryRepository>();
                services.AddSingleton(sp => new AgentCatalogProvider(
                    sp.GetRequiredService<IOptions<AgentOrionOptions>>(),
                    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentOrion.Api"))));
                services.AddSingleton<AgentRequestRouter>();
            });
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            Dispose();

            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }
            }
            catch
            {
                await Task.CompletedTask;
            }
        }
    }

}
