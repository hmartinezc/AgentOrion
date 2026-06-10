using AgentOrion.Core.Models;

namespace AgentOrion.Core.Operations;

public interface IAwbReservationGateway
{
    Task<AwbReservationOperationResult> CreateReservationAsync(AwbReservationCreateRequest request, CancellationToken ct = default);
    Task<AwbReservationOperationResult> GetReservationAsync(string awbNumber, CancellationToken ct = default);
    Task<AwbReservationOperationResult> UpdateReservationStatusAsync(AwbReservationStatusUpdateRequest request, CancellationToken ct = default);
    Task<AwbReservationOperationResult> CancelReservationAsync(AwbReservationCancelRequest request, CancellationToken ct = default);
}

public sealed record AwbReservationCreateRequest(
    string ProductType,
    string ProductName,
    double QuantityKg,
    string OriginAirport,
    string DestinationAirport,
    int? CustomerId = null,
    string? FlightDate = null);

public sealed record AwbReservationStatusUpdateRequest(string AwbNumber, string Status);

public sealed record AwbReservationCancelRequest(string AwbNumber, string? Reason = null);

public sealed class AwbReservationDto
{
    public int Id { get; init; }
    public string? AwbNumber { get; init; }
    public int? CustomerId { get; init; }
    public string ProductType { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public double? QuantityKg { get; init; }
    public double? TemperatureRequiredC { get; init; }
    public string? OriginAirport { get; init; }
    public string? DestinationAirport { get; init; }
    public DateTime? FlightDate { get; init; }
    public string Status { get; init; } = ShipmentStatuses.Requested;
    public string? PhytosanitaryCert { get; init; }
    public DateTime CreatedAt { get; init; }

    public static AwbReservationDto FromShipment(Shipment shipment) => new()
    {
        Id = shipment.Id,
        AwbNumber = shipment.AwbNumber,
        CustomerId = shipment.CustomerId,
        ProductType = shipment.ProductType,
        ProductName = shipment.ProductName,
        QuantityKg = shipment.QuantityKg,
        TemperatureRequiredC = shipment.TemperatureRequiredC,
        OriginAirport = shipment.OriginAirport,
        DestinationAirport = shipment.DestinationAirport,
        FlightDate = shipment.FlightDate,
        Status = shipment.Status,
        PhytosanitaryCert = shipment.PhytosanitaryCert,
        CreatedAt = shipment.CreatedAt
    };
}

public sealed class AwbReservationOperationResult
{
    public bool Success { get; init; }
    public AwbReservationDto? Reservation { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static AwbReservationOperationResult Ok(AwbReservationDto reservation) => new()
    {
        Success = true,
        Reservation = reservation
    };

    public static AwbReservationOperationResult NotFound(string awbNumber) => new()
    {
        Success = false,
        ErrorCode = "not_found",
        ErrorMessage = $"No se encontro el AWB {awbNumber}."
    };

    public static AwbReservationOperationResult Invalid(string message) => new()
    {
        Success = false,
        ErrorCode = "invalid_request",
        ErrorMessage = message
    };

    public static AwbReservationOperationResult Failed(string message, string errorCode = "operation_failed") => new()
    {
        Success = false,
        ErrorCode = errorCode,
        ErrorMessage = message
    };
}
