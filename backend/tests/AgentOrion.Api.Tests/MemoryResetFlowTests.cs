using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AgentOrion.Api.Endpoints;
using AgentOrion.Core.Configuration;
using AgentOrion.Core.Operations;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Copilot;
using AgentOrion.Infrastructure.Tools;
using AgentOrion.Infrastructure.Persistence;
using AgentOrion.Infrastructure.Persistence.Repositories;
using GitHub.Copilot.SDK;
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
    public async Task DatabaseHealthEndpoint_ReturnsSchemaAndConnectionSettings()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();

        var status = await client.GetFromJsonAsync<AgentOrion.Core.Models.DatabaseHealthStatus>("/api/db/health");

        Assert.NotNull(status);
        Assert.True(status!.CanConnect);
        Assert.Equal(TursoContext.CurrentSchemaVersion, status.SchemaVersion);
        Assert.Equal("sqlite", status.Provider);
        Assert.True(status.ForeignKeysEnabled);
        Assert.True(status.BusyTimeoutMs >= 5000);
        Assert.False(string.IsNullOrWhiteSpace(status.DatabasePath));
    }

    [Fact]
    public async Task RuntimeEndpoint_ReturnsNonSecretAgentDiagnostics()
    {
        await using var app = new TestApplication();
        using var client = app.CreateClient();

        var runtime = await client.GetFromJsonAsync<RuntimeInfo>("/api/runtime");

        Assert.NotNull(runtime);
        Assert.Equal("Code Name Orion", runtime!.AgentName);
        Assert.Equal("test-model", runtime.Model);
        Assert.Equal(ChatModes.Memory, runtime.DefaultMode);
        Assert.Contains(ChatModes.Fast, runtime.SupportedModes);
        Assert.True(runtime.ProviderConfigured);
        Assert.True(runtime.SkillCount > 0);
        Assert.True(runtime.RouteCount > 0);
    }

    [Fact]
    public async Task TursoContext_AppliesMigrationsAndConnectionPragmasIdempotently()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"agentorion-migration-{Guid.NewGuid():N}.db");

        try
        {
            _ = new TursoContext(dbPath);
            var context = new TursoContext(dbPath);

            using var connection = context.CreateConnection();

            Assert.Equal(TursoContext.CurrentSchemaVersion, Convert.ToInt32(await ScalarAsync(connection, "SELECT MAX(Version) FROM SchemaMigrations;")));
            Assert.Equal(TursoContext.CurrentSchemaVersion, Convert.ToInt32(await ScalarAsync(connection, "SELECT COUNT(*) FROM SchemaMigrations;")));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(connection, "PRAGMA foreign_keys;")));
            Assert.True(Convert.ToInt32(await ScalarAsync(connection, "PRAGMA busy_timeout;")) >= 5000);

            var journalMode = Convert.ToString(await ScalarAsync(connection, "PRAGMA journal_mode;"));
            Assert.Equal("wal", journalMode, ignoreCase: true);
        }
        finally
        {
            TryDeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task ShipmentRepository_CreateWithEvent_CommitsShipmentAndTimelineAtomically()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();
        var shipmentRepository = scope.ServiceProvider.GetRequiredService<IShipmentRepository>();
        var eventRepository = scope.ServiceProvider.GetRequiredService<IShipmentEventRepository>();

        var shipment = new AgentOrion.Core.Models.Shipment
        {
            AwbNumber = "AWB-FLO-TRANSACTION-01",
            ProductType = "flores",
            ProductName = "Rosas",
            QuantityKg = 25,
            TemperatureRequiredC = 2,
            OriginAirport = "BOG",
            DestinationAirport = "MIA",
            Status = AgentOrion.Core.Models.ShipmentStatuses.Requested
        };

        var shipmentId = await shipmentRepository.CreateWithEventAsync(
            shipment,
            "awb_created",
            "{\"source\":\"test\"}");

        var savedShipment = await shipmentRepository.GetByIdAsync(shipmentId);
        var events = await eventRepository.GetByAwbAsync("AWB-FLO-TRANSACTION-01");

        Assert.NotNull(savedShipment);
        Assert.Single(events);
        Assert.Equal("awb_created", events[0].EventType);
        Assert.Equal(shipmentId, events[0].ShipmentId);
    }

    [Fact]
    public async Task AuditAndSimulatedEmailRepositories_PersistAgentOperationalData()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();
        var shipmentRepository = scope.ServiceProvider.GetRequiredService<IShipmentRepository>();
        var auditRepository = scope.ServiceProvider.GetRequiredService<IAgentAuditRepository>();
        var emailRepository = scope.ServiceProvider.GetRequiredService<ISimulatedEmailRepository>();

        var shipmentId = await shipmentRepository.CreateAsync(new AgentOrion.Core.Models.Shipment
        {
            AwbNumber = "AWB-AUDIT-EMAIL-01",
            ProductType = "flores",
            Status = AgentOrion.Core.Models.ShipmentStatuses.Requested
        });

        await auditRepository.CreateAsync(new AgentOrion.Core.Models.AgentAuditLog
        {
            SessionId = "audit-session",
            UserPrompt = "consulta awb",
            AgentResponse = "AWB encontrado",
            RouteName = "awb-dispatch",
            Model = "test-model",
            ToolsJson = "[\"get_awb_status\"]",
            DurationMs = 12.5
        });

        await emailRepository.CreateAsync(new AgentOrion.Core.Models.SimulatedEmail
        {
            ShipmentId = shipmentId,
            RecipientEmail = "ops@example.com",
            Subject = "AWB",
            Body = "Reserva creada"
        });

        var audits = await auditRepository.GetBySessionAsync("audit-session");
        var emails = await emailRepository.GetByAwbAsync("AWB-AUDIT-EMAIL-01");

        Assert.Single(audits);
        Assert.Equal("awb-dispatch", audits[0].RouteName);
        Assert.Single(emails);
        Assert.Equal("ops@example.com", emails[0].RecipientEmail);
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
            scope.ServiceProvider.GetRequiredService<IAwbReservationGateway>());

        Assert.NotNull(result);
        Assert.Equal("operations-general", result!.RouteName);
        Assert.Contains("soy Orion", result.Content);
        Assert.Empty(result.Tools);
    }

    [Fact]
    public async Task WorkflowCoordinator_UsesFirstWorkflowThatHandlesPrompt()
    {
        var coordinator = new WorkflowCoordinator([
            new NullWorkflow(),
            new StubWorkflow("awb-dispatch")
        ]);

        var result = await coordinator.TryHandleAsync(new WorkflowContext(
            "crear reserva AWB",
            null,
            "workflow-test",
            ChatModes.Memory));

        Assert.NotNull(result);
        Assert.Equal("awb-dispatch", result!.RouteName);
        Assert.Equal("handled", result.Content);
    }

    [Fact]
    public async Task AwbReservationDraftExtractor_CombinesPromptAndMemorySignals()
    {
        var extractor = new AwbReservationDraftExtractor();
        var memoryService = new ConversationMemoryService();
        var memory = memoryService.Create("extractor-test");
        memory.CurrentIntent = "awb_creation";
        memory.Customer.FullName = "JAF Flower";
        memoryService.ApplyPrompt(memory, "reserva AWB para flores rosas, 25 kg, origen BOG, destino MIA, fecha 2026-05-20");

        var result = await extractor.ExtractAsync(new WorkflowContext(
            "producto rosas 25 kg origen BOG destino MIA",
            memory,
            "extractor-test",
            ChatModes.Memory));

        Assert.True(result.Draft.HasReservationContext);
        Assert.True(result.Draft.HasCustomerSignal);
        Assert.True(result.Draft.HasShipmentSignal);
        Assert.Equal("JAF Flower", result.Draft.CustomerName);
        Assert.False(string.IsNullOrWhiteSpace(result.Draft.ProductName));
        Assert.Equal(25, result.Draft.QuantityKg);
        Assert.Equal("BOG", result.Draft.OriginAirport);
        Assert.Equal("MIA", result.Draft.DestinationAirport);
        Assert.Equal("rules", result.Source);
        Assert.True(result.Confidence >= 0.7);
    }

    [Fact]
    public void AwbReservationDraftValidator_ReturnsNotApplicableForUnrelatedPrompt()
    {
        var validator = new AwbReservationDraftValidator();
        var extraction = new WorkflowExtractionResult<AwbReservationDraft>(
            new AwbReservationDraft(
                HasReservationIntent: false,
                HasReservationContext: false,
                HasCustomerSignal: false,
                HasShipmentSignal: false,
                CurrentIntent: null),
            Source: "rules",
            Confidence: 0.25);

        var validation = validator.Validate(extraction, new WorkflowContext(
            "hola",
            null,
            "validator-test",
            ChatModes.Memory));

        Assert.False(validation.Applies);
        Assert.False(validation.CanExecute);
        Assert.Empty(validation.MissingFields);
    }

    [Fact]
    public void AwbReservationDraftValidator_FindsMissingOperationalData()
    {
        var validator = new AwbReservationDraftValidator();
        var extraction = new WorkflowExtractionResult<AwbReservationDraft>(
            new AwbReservationDraft(
                HasReservationIntent: true,
                HasReservationContext: false,
                HasCustomerSignal: false,
                HasShipmentSignal: true,
                CurrentIntent: null),
            Source: "rules",
            Confidence: 0.85);

        var validation = validator.Validate(extraction, new WorkflowContext(
            "crear reserva AWB flores 25 kg origen BOG destino MIA",
            null,
            "validator-test",
            ChatModes.Memory));

        Assert.True(validation.Applies);
        Assert.False(validation.CanExecute);
        Assert.Contains("cliente", validation.MissingFields);
    }

    [Fact]
    public void AwbReservationDraftValidator_AllowsExecutionWhenRequiredDataExists()
    {
        var validator = new AwbReservationDraftValidator();
        var extraction = new WorkflowExtractionResult<AwbReservationDraft>(
            new AwbReservationDraft(
                HasReservationIntent: true,
                HasReservationContext: false,
                HasCustomerSignal: true,
                HasShipmentSignal: true,
                CurrentIntent: null,
                CustomerId: 12,
                CustomerName: "JAF Flower",
                ProductType: "flores",
                ProductName: "Rosas",
                QuantityKg: 25,
                OriginAirport: "BOG",
                DestinationAirport: "MIA"),
            Source: "rules",
            Confidence: 0.9);

        var validation = validator.Validate(extraction, new WorkflowContext(
            "crear reserva AWB para cliente JAF flores 25 kg origen BOG destino MIA",
            null,
            "validator-test",
            ChatModes.Memory));

        Assert.True(validation.Applies);
        Assert.True(validation.CanExecute);
        Assert.Empty(validation.MissingFields);
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
            scope.ServiceProvider.GetRequiredService<IAwbReservationGateway>());

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
            scope.ServiceProvider.GetRequiredService<IAwbReservationGateway>());

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
            scope.ServiceProvider.GetRequiredService<IAwbReservationGateway>());

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
        var awbReservations = scope.ServiceProvider.GetRequiredService<IAwbReservationGateway>();
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
            awbReservations);

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
            awbReservations);

        Assert.NotNull(followUp);
        Assert.Equal("awb-dispatch", followUp!.RouteName);
        Assert.Empty(followUp.Tools);
        Assert.Contains("Si, la reserva AWB esta creada", followUp.Content);
        Assert.Contains(memory.Shipment.AwbNumber, followUp.Content);
        Assert.Contains("Pedro Valdair", followUp.Content);
    }

    [Fact]
    public async Task FakeAwbReservationGateway_HandlesCreateGetUpdateCancelAndErrors()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IAwbReservationGateway>();

        var created = await gateway.CreateReservationAsync(new AwbReservationCreateRequest(
            "flores",
            "Rosas",
            25,
            "BOG",
            "MIA",
            FlightDate: "2026-05-20"));

        Assert.True(created.Success);
        Assert.NotNull(created.Reservation);
        Assert.False(string.IsNullOrWhiteSpace(created.Reservation!.AwbNumber));
        Assert.Equal("solicitado", created.Reservation.Status);

        var fetched = await gateway.GetReservationAsync(created.Reservation.AwbNumber!);
        Assert.True(fetched.Success);
        Assert.Equal(created.Reservation.AwbNumber, fetched.Reservation!.AwbNumber);

        var updated = await gateway.UpdateReservationStatusAsync(new AwbReservationStatusUpdateRequest(
            created.Reservation.AwbNumber!,
            AgentOrion.Core.Models.ShipmentStatuses.Confirmed));
        Assert.True(updated.Success);
        Assert.Equal(AgentOrion.Core.Models.ShipmentStatuses.Confirmed, updated.Reservation!.Status);

        var invalidStatus = await gateway.UpdateReservationStatusAsync(new AwbReservationStatusUpdateRequest(
            created.Reservation.AwbNumber!,
            "cerrado"));
        Assert.False(invalidStatus.Success);
        Assert.Equal("invalid_request", invalidStatus.ErrorCode);

        var cancelled = await gateway.CancelReservationAsync(new AwbReservationCancelRequest(
            created.Reservation.AwbNumber!,
            "cliente cancela prueba"));
        Assert.True(cancelled.Success);
        Assert.Equal(AgentOrion.Core.Models.ShipmentStatuses.Cancelled, cancelled.Reservation!.Status);

        var missing = await gateway.GetReservationAsync("AWB-NO-EXISTE");
        Assert.False(missing.Success);
        Assert.Equal("not_found", missing.ErrorCode);
    }

    [Fact]
    public async Task AwbToolService_UsesReservationGatewayForAwbOperations()
    {
        await using var app = new TestApplication();
        using var scope = app.Services.CreateScope();
        var service = new AwbToolService(scope.ServiceProvider.GetRequiredService<IAwbReservationGateway>());

        var createPayload = JsonSerializer.Serialize(await service.CreateAwbAsync(
            "mariscos",
            "Cangrejo",
            500,
            "BOG",
            "MIA",
            flightDate: "2026-05-22"));
        using var createDocument = JsonDocument.Parse(createPayload);
        var awbNumber = createDocument.RootElement.GetProperty("awbNumber").GetString();

        Assert.False(string.IsNullOrWhiteSpace(awbNumber));
        Assert.Equal("solicitado", createDocument.RootElement.GetProperty("status").GetString());

        var updatePayload = JsonSerializer.Serialize(await service.UpdateAwbStatusAsync(
            awbNumber!,
            AgentOrion.Core.Models.ShipmentStatuses.Confirmed));
        using var updateDocument = JsonDocument.Parse(updatePayload);

        Assert.True(updateDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(AgentOrion.Core.Models.ShipmentStatuses.Confirmed, updateDocument.RootElement.GetProperty("Status").GetString());

        var cancelPayload = JsonSerializer.Serialize(await service.CancelAwbAsync(awbNumber!, "test"));
        using var cancelDocument = JsonDocument.Parse(cancelPayload);

        Assert.True(cancelDocument.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(AgentOrion.Core.Models.ShipmentStatuses.Cancelled, cancelDocument.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public void ToolCatalog_RejectsUnknownToolsInAgentCatalog()
    {
        var catalog = new AgentCatalog
        {
            DefaultRoute = "test-route",
            Routes =
            [
                new AgentRouteDefinition
                {
                    Name = "test-route",
                    DisplayName = "Test Route",
                    SpecialistPrompt = "Test",
                    ToolNames = ["missing_tool"]
                }
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => ToolCatalog.ValidateCatalog(catalog));
        Assert.Contains("missing_tool", ex.Message);
    }

    [Fact]
    public void AgentCatalog_RejectsEmptyRouteDisplayNameAndPrompt()
    {
        var catalogPath = Path.Combine(Path.GetTempPath(), $"agentorion-catalog-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(catalogPath, """
                {
                  "defaultRoute": "test-route",
                  "mixedRoute": {
                    "name": "mixed",
                    "displayName": "Mixed",
                    "specialistPrompt": "Coordinate mixed work."
                  },
                  "routes": [
                    {
                      "name": "test-route",
                      "displayName": "",
                      "specialistPrompt": "Test"
                    }
                  ]
                }
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => AgentCatalogProvider.Load(catalogPath));
            Assert.Contains("empty display name", ex.Message);

            File.WriteAllText(catalogPath, """
                {
                  "defaultRoute": "test-route",
                  "mixedRoute": {
                    "name": "mixed",
                    "displayName": "Mixed",
                    "specialistPrompt": "Coordinate mixed work."
                  },
                  "routes": [
                    {
                      "name": "test-route",
                      "displayName": "Test Route",
                      "specialistPrompt": ""
                    }
                  ]
                }
                """);

            ex = Assert.Throws<InvalidOperationException>(() => AgentCatalogProvider.Load(catalogPath));
            Assert.Contains("empty specialist prompt", ex.Message);
        }
        finally
        {
            if (File.Exists(catalogPath))
            {
                File.Delete(catalogPath);
            }
        }
    }

    [Fact]
    public void SkillRegistry_RejectsMissingSkillsInAgentCatalog()
    {
        var skillRoot = Path.Combine(Path.GetTempPath(), $"agentorion-skills-{Guid.NewGuid():N}");
        var coreSkill = Path.Combine(skillRoot, "core-domain");

        try
        {
            Directory.CreateDirectory(coreSkill);
            File.WriteAllText(Path.Combine(coreSkill, "SKILL.md"), "# Core");

            var registry = new SkillRegistry(
                Options.Create(new AgentOrionOptions { SkillDirectories = [skillRoot] }),
                AppContext.BaseDirectory);
            var catalog = new AgentCatalog
            {
                DefaultRoute = "test-route",
                Routes =
                [
                    new AgentRouteDefinition
                    {
                        Name = "test-route",
                        DisplayName = "Test Route",
                        SpecialistPrompt = "Test",
                        SkillNames = ["core-domain", "missing-skill"]
                    }
                ]
            };

            var ex = Assert.Throws<InvalidOperationException>(() => registry.ValidateCatalog(catalog));
            Assert.Contains("missing-skill", ex.Message);
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void AgentPermissionPolicy_ClassifiesOperationalToolsAsSensitive()
    {
        var policy = new AgentPermissionPolicy();

        Assert.True(policy.RequiresConfirmation("create_awb"));
        Assert.True(policy.RequiresConfirmation("cancel_awb"));
        Assert.False(policy.RequiresConfirmation("get_awb_status"));
        Assert.False(policy.RequiresConfirmation(null));
    }

    [Fact]
    public void AgentResponseTraceBuilder_RecordsToolExecutionObservability()
    {
        var trace = new AgentResponseTraceBuilder("trace-session", "cancelar awb", "test-model");

        trace.RecordToolStart("call-1", "cancel_awb");
        trace.RecordToolPermission("call-1", requiresConfirmation: true);
        trace.RecordToolResult("call-1", "cancel_awb", success: false, error: "conflict", resultPreview: "{\"error\":\"conflict\"}");
        trace.Complete();

        var built = trace.Build();

        var execution = Assert.Single(built.ToolExecutions);
        Assert.Equal("cancel_awb", execution.ToolName);
        Assert.True(execution.RequiresConfirmation);
        Assert.False(execution.Success);
        Assert.Equal("conflict", execution.Error);
        Assert.Contains("conflict", execution.ResultPreview);
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
        Assert.Equal("qwen3.5-plus", profile.Model);
        Assert.NotNull(profile.Provider);
        Assert.Equal("https://opencode.ai/zen/v1", profile.Provider!.BaseUrl);

        var awbWithProductProfile = router.SelectProfile(
            "crear reserva AWB para flores",
            new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("awb-dispatch", awbWithProductProfile.RouteName);
        Assert.Contains("awb-dispatch", awbWithProductProfile.SkillNames);
    }

    [Fact]
    public void AgentRouter_UsesIntentScoringForColdChainAndMixedOperationalRequests()
    {
        var catalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentOrion.Api", "agent-catalog.json"));
        var catalogProvider = new AgentCatalogProvider(
            Options.Create(new AgentOrionOptions { AgentCatalogPath = catalogPath }),
            Path.GetDirectoryName(catalogPath)!);
        var router = new AgentRequestRouter(catalogProvider);

        var coldChainProfile = router.SelectProfile(
            "validar temperatura para flores",
            new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("cold-chain", coldChainProfile.RouteName);
        Assert.Contains("cold-chain", coldChainProfile.SkillNames);

        var mixedProfile = router.SelectProfile(
            "notificar al cliente una alerta de temperatura del AWB",
            new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("mixed-operations", mixedProfile.RouteName);
        Assert.Contains("awb-dispatch", mixedProfile.SkillNames);
        Assert.Contains("cold-chain", mixedProfile.SkillNames);
        Assert.Contains("client-comm", mixedProfile.SkillNames);
    }

    [Fact]
    public async Task AgentRouter_EvaluatesIntentDatasetWithConfidenceAndExplanations()
    {
        var catalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentOrion.Api", "agent-catalog.json"));
        var catalogProvider = new AgentCatalogProvider(
            Options.Create(new AgentOrionOptions { AgentCatalogPath = catalogPath }),
            Path.GetDirectoryName(catalogPath)!);
        var router = new AgentRequestRouter(catalogProvider);
        var toolCatalog = new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase);
        var datasetPath = Path.Combine(AppContext.BaseDirectory, "routing-evaluation-cases.json");
        var cases = JsonSerializer.Deserialize<RoutingEvaluationCase[]>(
            await File.ReadAllTextAsync(datasetPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(cases);
        Assert.NotEmpty(cases!);

        foreach (var testCase in cases!)
        {
            var decision = router.SelectProfileWithTrace(testCase.Prompt, toolCatalog);

            Assert.Equal(testCase.ExpectedRouteName, decision.Profile.RouteName);
            Assert.Equal(testCase.ExpectedRouteName, decision.Trace.SelectedRouteName);
            Assert.True(
                decision.Trace.Confidence >= testCase.MinimumConfidence,
                $"{testCase.Name} expected confidence >= {testCase.MinimumConfidence}, actual {decision.Trace.Confidence}.");
            Assert.False(string.IsNullOrWhiteSpace(decision.Trace.Reason));
            Assert.NotEmpty(decision.Trace.Candidates);
        }
    }

    [Fact]
    public void AgentCatalog_FallsBackToGlobalConfigWhenRouteHasNoProvider()
    {
        var catalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentOrion.Api", "agent-catalog.json"));
        var catalogProvider = new AgentCatalogProvider(
            Options.Create(new AgentOrionOptions { AgentCatalogPath = catalogPath }),
            Path.GetDirectoryName(catalogPath)!);
        var router = new AgentRequestRouter(catalogProvider);
        var profile = router.SelectProfile(
            "hola",
            new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("operations-general", profile.RouteName);
        Assert.Null(profile.Model);
        Assert.Null(profile.Provider);

        var decision = router.SelectProfileWithTrace(
            "hola",
            new Dictionary<string, AIFunction>(StringComparer.OrdinalIgnoreCase));
        Assert.True(decision.Trace.RequiresMiniRouterReview);
        Assert.Equal("rules-low-confidence", decision.Trace.RoutingMode);
    }

    [Fact]
    public void AgentFactory_MergesRouteProviderWithGlobalApiKey()
    {
        var routeProvider = new ProviderConfig
        {
            Type = "openai",
            BaseUrl = "https://opencode.ai/zen/v1",
            WireApi = "chat/completions"
        };
        var globalProvider = new ProviderOptions
        {
            Type = "openai",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "test-key",
            WireApi = "completions"
        };

        var method = typeof(AgentFactory).GetMethod("BuildEffectiveProvider", BindingFlags.NonPublic | BindingFlags.Static);
        var merged = (ProviderConfig)method!.Invoke(null, new object?[] { routeProvider, globalProvider })!;

        Assert.Equal("openai", merged.Type);
        Assert.Equal("https://opencode.ai/zen/v1", merged.BaseUrl);
        Assert.Equal("chat/completions", merged.WireApi);
        Assert.Equal("test-key", merged.ApiKey);
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

    private static async Task<object?> ScalarAsync(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static void TryDeleteSqliteFiles(string dbPath)
    {
        foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best effort cleanup for files that may still be closing on Windows.
            }
        }
    }

    private sealed record RoutingEvaluationCase(
        string Name,
        string Prompt,
        string ExpectedRouteName,
        double MinimumConfidence);

    private sealed record RuntimeInfo(
        string AgentName,
        string Model,
        string DefaultMode,
        string[] SupportedModes,
        bool ProviderConfigured,
        int SkillCount,
        int RouteCount);

    private sealed class NullWorkflow : IAgentWorkflow
    {
        public Task<DeterministicTurnResult?> TryHandleAsync(
            WorkflowContext context,
            CancellationToken ct = default) =>
            Task.FromResult<DeterministicTurnResult?>(null);
    }

    private sealed class StubWorkflow : IAgentWorkflow
    {
        private readonly string _routeName;

        public StubWorkflow(string routeName)
        {
            _routeName = routeName;
        }

        public Task<DeterministicTurnResult?> TryHandleAsync(
            WorkflowContext context,
            CancellationToken ct = default) =>
            Task.FromResult<DeterministicTurnResult?>(new DeterministicTurnResult
            {
                RouteName = _routeName,
                RouteDisplayName = "Stub",
                Content = "handled"
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
                services.RemoveAll<IAgentOrionDbConnectionFactory>();
                services.RemoveAll<IConversationMemoryRepository>();
                services.RemoveAll<AgentCatalogProvider>();
                services.RemoveAll<AgentRequestRouter>();

                services.AddSingleton(new TursoContext(_dbPath));
                services.AddSingleton<IAgentOrionDbConnectionFactory>(sp => sp.GetRequiredService<TursoContext>());
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
