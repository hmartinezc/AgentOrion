using AgentOrion.Core.Models;
using AgentOrion.Core.Operations;
using AgentOrion.Core.Persistence;

namespace AgentOrion.Infrastructure.Copilot;

public sealed record WorkflowContext(
    string Prompt,
    ConversationMemoryState? MemoryState,
    string SessionId,
    string ChatMode);

public interface IAgentWorkflow
{
    Task<DeterministicTurnResult?> TryHandleAsync(
        WorkflowContext context,
        CancellationToken ct = default);
}

public interface IWorkflowInputExtractor<TDraft>
{
    Task<WorkflowExtractionResult<TDraft>> ExtractAsync(
        WorkflowContext context,
        CancellationToken ct = default);
}

public interface IWorkflowValidator<TDraft>
{
    WorkflowValidationResult Validate(
        WorkflowExtractionResult<TDraft> extraction,
        WorkflowContext context);
}

public interface IWorkflowResponseComposer<TDraft>
{
    string ComposeMissingFields(
        WorkflowExtractionResult<TDraft> extraction,
        WorkflowValidationResult validation);
}

public sealed record WorkflowExtractionResult<TDraft>(
    TDraft Draft,
    string Source,
    double Confidence);

public sealed class WorkflowValidationResult
{
    private WorkflowValidationResult(
        bool applies,
        bool canExecute,
        IReadOnlyList<string> missingFields,
        IReadOnlyList<string> warnings)
    {
        Applies = applies;
        CanExecute = canExecute;
        MissingFields = missingFields;
        Warnings = warnings;
    }

    public bool Applies { get; }
    public bool CanExecute { get; }
    public IReadOnlyList<string> MissingFields { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static WorkflowValidationResult NotApplicable() =>
        new(false, false, Array.Empty<string>(), Array.Empty<string>());

    public static WorkflowValidationResult Ready(params string[] warnings) =>
        new(true, true, Array.Empty<string>(), warnings);

    public static WorkflowValidationResult NeedsMoreData(params string[] missingFields) =>
        new(true, false, missingFields, Array.Empty<string>());
}

public sealed class WorkflowCoordinator
{
    private readonly IReadOnlyList<IAgentWorkflow> _workflows;

    public WorkflowCoordinator(IEnumerable<IAgentWorkflow> workflows)
    {
        _workflows = workflows.ToArray();
    }

    public async Task<DeterministicTurnResult?> TryHandleAsync(
        WorkflowContext context,
        CancellationToken ct = default)
    {
        foreach (var workflow in _workflows)
        {
            ct.ThrowIfCancellationRequested();

            var result = await workflow.TryHandleAsync(context, ct);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}

public sealed record AwbReservationDraft(
    bool HasReservationIntent,
    bool HasReservationContext,
    bool HasCustomerSignal,
    bool HasShipmentSignal,
    string? CurrentIntent,
    int? CustomerId = null,
    string? CustomerName = null,
    string? AwbNumber = null,
    string? ProductType = null,
    string? ProductName = null,
    double? QuantityKg = null,
    string? OriginAirport = null,
    string? DestinationAirport = null,
    DateTime? FlightDate = null);

public sealed class AwbReservationDraftExtractor : IWorkflowInputExtractor<AwbReservationDraft>
{
    public Task<WorkflowExtractionResult<AwbReservationDraft>> ExtractAsync(
        WorkflowContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = Normalize(context.Prompt);
        var customer = context.MemoryState?.Customer;
        var shipment = context.MemoryState?.Shipment;
        var hasReservationIntent =
            normalized.Contains("reserv") ||
            normalized.Contains("booking") ||
            normalized.Contains("awb") ||
            normalized.Contains("despacho") ||
            normalized.Contains("guia") ||
            normalized.Contains("embarque");
        var hasReservationContext = string.Equals(
            context.MemoryState?.CurrentIntent,
            "awb_creation",
            StringComparison.OrdinalIgnoreCase);
        var hasCustomerSignal =
            normalized.Contains("cliente") ||
            normalized.Contains("customer") ||
            normalized.Contains("empresa") ||
            customer?.CustomerId is not null ||
            !string.IsNullOrWhiteSpace(customer?.FullName);
        var hasShipmentSignal =
            normalized.Contains("producto") ||
            normalized.Contains("ruta") ||
            normalized.Contains("origen") ||
            normalized.Contains("destino") ||
            normalized.Contains("kg") ||
            normalized.Contains("pieza") ||
            shipment?.QuantityKg is not null ||
            !string.IsNullOrWhiteSpace(shipment?.ProductType) ||
            !string.IsNullOrWhiteSpace(shipment?.ProductName) ||
            !string.IsNullOrWhiteSpace(shipment?.OriginAirport) ||
            !string.IsNullOrWhiteSpace(shipment?.DestinationAirport);

        var confidence = hasReservationIntent
            ? 0.85
            : hasReservationContext && hasShipmentSignal
                ? 0.7
                : 0.25;

        var draft = new AwbReservationDraft(
            hasReservationIntent,
            hasReservationContext,
            hasCustomerSignal,
            hasShipmentSignal,
            context.MemoryState?.CurrentIntent,
            customer?.CustomerId,
            customer?.FullName ?? customer?.CompanyName,
            shipment?.AwbNumber,
            shipment?.ProductType,
            shipment?.ProductName,
            shipment?.QuantityKg,
            shipment?.OriginAirport,
            shipment?.DestinationAirport,
            shipment?.FlightDate);

        return Task.FromResult(new WorkflowExtractionResult<AwbReservationDraft>(
            draft,
            Source: "rules",
            Confidence: confidence));
    }

    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant()
            .Replace("á", "a")
            .Replace("é", "e")
            .Replace("í", "i")
            .Replace("ó", "o")
            .Replace("ú", "u")
            .Replace("ñ", "n");
}

public sealed class AwbReservationDraftValidator : IWorkflowValidator<AwbReservationDraft>
{
    public WorkflowValidationResult Validate(
        WorkflowExtractionResult<AwbReservationDraft> extraction,
        WorkflowContext context)
    {
        var draft = extraction.Draft;
        if (!draft.HasReservationIntent && !(draft.HasReservationContext && draft.HasShipmentSignal))
        {
            return WorkflowValidationResult.NotApplicable();
        }

        var missing = new List<string>();
        if (!draft.HasCustomerSignal)
        {
            missing.Add("cliente");
        }

        if (!draft.HasShipmentSignal)
        {
            missing.Add("datos de envio");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(draft.ProductType) || string.IsNullOrWhiteSpace(draft.ProductName))
            {
                missing.Add("producto");
            }

            if (!draft.QuantityKg.HasValue)
            {
                missing.Add("cantidad en kg");
            }

            if (string.IsNullOrWhiteSpace(draft.OriginAirport))
            {
                missing.Add("origen");
            }

            if (string.IsNullOrWhiteSpace(draft.DestinationAirport))
            {
                missing.Add("destino");
            }
        }

        return missing.Count == 0
            ? WorkflowValidationResult.Ready()
            : WorkflowValidationResult.NeedsMoreData(missing.ToArray());
    }
}

public sealed class AwbReservationResponseComposer : IWorkflowResponseComposer<AwbReservationDraft>
{
    public string ComposeMissingFields(
        WorkflowExtractionResult<AwbReservationDraft> extraction,
        WorkflowValidationResult validation)
    {
        var missing = validation.MissingFields.Count == 0
            ? "los datos operativos requeridos"
            : string.Join(" y ", validation.MissingFields);

        return $"Para continuar con la reserva AWB necesito confirmar {missing}.";
    }
}

public sealed class AwbReservationWorkflow : IAgentWorkflow
{
    private readonly IWorkflowInputExtractor<AwbReservationDraft> _extractor;
    private readonly IWorkflowValidator<AwbReservationDraft> _validator;
    private readonly IWorkflowResponseComposer<AwbReservationDraft> _responseComposer;
    private readonly ConversationMemoryService _memoryService;
    private readonly ICustomerRepository _customers;
    private readonly IAwbReservationGateway _awbReservations;

    public AwbReservationWorkflow(
        IWorkflowInputExtractor<AwbReservationDraft> extractor,
        IWorkflowValidator<AwbReservationDraft> validator,
        IWorkflowResponseComposer<AwbReservationDraft> responseComposer,
        ConversationMemoryService memoryService,
        ICustomerRepository customers,
        IAwbReservationGateway awbReservations)
    {
        _extractor = extractor;
        _validator = validator;
        _responseComposer = responseComposer;
        _memoryService = memoryService;
        _customers = customers;
        _awbReservations = awbReservations;
    }

    public async Task<DeterministicTurnResult?> TryHandleAsync(
        WorkflowContext context,
        CancellationToken ct = default)
    {
        var extraction = await _extractor.ExtractAsync(context, ct);
        var validation = _validator.Validate(extraction, context);
        if (!validation.Applies)
        {
            return null;
        }

        var result = await DeterministicTurnHandler.TryHandleAsync(
            context.Prompt,
            context.MemoryState,
            _memoryService,
            _customers,
            _awbReservations,
            ct);

        if (result is not null || validation.CanExecute)
        {
            return result;
        }

        return new DeterministicTurnResult
        {
            RouteName = "awb-dispatch",
            RouteDisplayName = "Despacho AWB",
            Content = _responseComposer.ComposeMissingFields(extraction, validation)
        };
    }
}
