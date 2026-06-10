using System.Text.Json;
using AgentOrion.Core.Models;
using AgentOrion.Core.Operations;
using AgentOrion.Core.Persistence;

namespace AgentOrion.Infrastructure.Operations;

public sealed class FakeAwbReservationGateway : IAwbReservationGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IShipmentRepository _shipments;

    public FakeAwbReservationGateway(IShipmentRepository shipments)
    {
        _shipments = shipments;
    }

    public async Task<AwbReservationOperationResult> CreateReservationAsync(AwbReservationCreateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var validation = ValidateCreateRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var temperatureRequiredC = ResolveTemperatureRequiredC(request.ProductType);
        var awbNumber = GenerateAwbNumber(request.ProductType);
        var shipment = new Shipment
        {
            AwbNumber = awbNumber,
            CustomerId = request.CustomerId,
            ProductType = request.ProductType.Trim().ToLowerInvariant(),
            ProductName = request.ProductName.Trim(),
            QuantityKg = request.QuantityKg,
            TemperatureRequiredC = temperatureRequiredC,
            OriginAirport = request.OriginAirport.Trim().ToUpperInvariant(),
            DestinationAirport = request.DestinationAirport.Trim().ToUpperInvariant(),
            FlightDate = TryParseFlightDate(request.FlightDate),
            Status = ShipmentStatuses.Requested,
            PhytosanitaryCert = $"PHYTO-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
        };

        var id = await _shipments.CreateWithEventAsync(
            shipment,
            "awb_created",
            JsonSerializer.Serialize(new { tempRequired = temperatureRequiredC, awb = awbNumber, source = "fake_gateway" }, JsonOptions));

        shipment.Id = id;
        return AwbReservationOperationResult.Ok(AwbReservationDto.FromShipment(shipment));
    }

    public async Task<AwbReservationOperationResult> GetReservationAsync(string awbNumber, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(awbNumber))
        {
            return AwbReservationOperationResult.Invalid("El numero de AWB es obligatorio.");
        }

        var shipment = await _shipments.GetByAwbAsync(awbNumber.Trim());
        return shipment is null
            ? AwbReservationOperationResult.NotFound(awbNumber)
            : AwbReservationOperationResult.Ok(AwbReservationDto.FromShipment(shipment));
    }

    public async Task<AwbReservationOperationResult> UpdateReservationStatusAsync(AwbReservationStatusUpdateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.AwbNumber))
        {
            return AwbReservationOperationResult.Invalid("El numero de AWB es obligatorio.");
        }

        var normalizedStatus = request.Status.Trim().ToLowerInvariant();
        if (!ShipmentStatuses.IsValid(normalizedStatus))
        {
            return AwbReservationOperationResult.Invalid($"Estado invalido: {request.Status}.");
        }

        var shipment = await _shipments.GetByAwbAsync(request.AwbNumber.Trim());
        if (shipment is null)
        {
            return AwbReservationOperationResult.NotFound(request.AwbNumber);
        }

        await _shipments.UpdateStatusAsync(shipment.Id, normalizedStatus);
        await _shipments.AddEventAsync(
            shipment.Id,
            "awb_status_updated",
            JsonSerializer.Serialize(new { oldStatus = shipment.Status, newStatus = normalizedStatus, source = "fake_gateway" }, JsonOptions));

        shipment.Status = normalizedStatus;
        return AwbReservationOperationResult.Ok(AwbReservationDto.FromShipment(shipment));
    }

    public async Task<AwbReservationOperationResult> CancelReservationAsync(AwbReservationCancelRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.AwbNumber))
        {
            return AwbReservationOperationResult.Invalid("El numero de AWB es obligatorio.");
        }

        var shipment = await _shipments.GetByAwbAsync(request.AwbNumber.Trim());
        if (shipment is null)
        {
            return AwbReservationOperationResult.NotFound(request.AwbNumber);
        }

        if (string.Equals(shipment.Status, ShipmentStatuses.Delivered, StringComparison.OrdinalIgnoreCase))
        {
            return AwbReservationOperationResult.Invalid("No se puede cancelar un AWB entregado.");
        }

        if (string.Equals(shipment.Status, ShipmentStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
        {
            return AwbReservationOperationResult.Invalid("El AWB ya esta cancelado.");
        }

        await _shipments.UpdateStatusAsync(shipment.Id, ShipmentStatuses.Cancelled);
        await _shipments.AddEventAsync(
            shipment.Id,
            "awb_cancelled",
            JsonSerializer.Serialize(new { oldStatus = shipment.Status, reason = request.Reason, source = "fake_gateway" }, JsonOptions));

        shipment.Status = ShipmentStatuses.Cancelled;
        return AwbReservationOperationResult.Ok(AwbReservationDto.FromShipment(shipment));
    }

    private static AwbReservationOperationResult? ValidateCreateRequest(AwbReservationCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProductType)) return AwbReservationOperationResult.Invalid("El tipo de producto es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.ProductName)) return AwbReservationOperationResult.Invalid("El nombre del producto es obligatorio.");
        if (request.QuantityKg <= 0) return AwbReservationOperationResult.Invalid("El peso debe ser mayor a cero.");
        if (string.IsNullOrWhiteSpace(request.OriginAirport)) return AwbReservationOperationResult.Invalid("El aeropuerto de origen es obligatorio.");
        if (string.IsNullOrWhiteSpace(request.DestinationAirport)) return AwbReservationOperationResult.Invalid("El aeropuerto de destino es obligatorio.");
        return null;
    }

    private static double ResolveTemperatureRequiredC(string productType) => productType.Trim().ToLowerInvariant() switch
    {
        "flores" => 2.0,
        "pescado" => -18.0,
        "mariscos" => -18.0,
        "frutas" => 8.0,
        _ => 2.0
    };

    private static string GenerateAwbNumber(string productType)
    {
        var normalized = new string(productType.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var prefix = normalized.Length >= 3 ? normalized[..3] : normalized.PadRight(3, 'X');
        return $"AWB-{prefix}-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";
    }

    private static DateTime? TryParseFlightDate(string? flightDate)
    {
        if (string.IsNullOrWhiteSpace(flightDate))
        {
            return null;
        }

        return DateTime.TryParse(flightDate, out var parsed)
            ? parsed
            : null;
    }
}
