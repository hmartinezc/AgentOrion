using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;
using AgentOrion.Core.Operations;

namespace AgentOrion.Infrastructure.Tools;

public class AwbToolService
{
    private readonly IAwbReservationGateway _reservations;

    public AwbToolService(IAwbReservationGateway reservations)
    {
        _reservations = reservations;
    }

    [Description("Crea un nuevo despacho AWB para carga perecedera. Devuelve el número de AWB generado.")]
    public async Task<object> CreateAwbAsync(
        [Description("Tipo de producto: flores, pescado, frutas, mariscos")] string productType,
        [Description("Nombre específico del producto, ej: Rosas, Tilapia, Banano")] string productName,
        [Description("Peso en kilogramos")] double quantityKg,
        [Description("Código IATA del aeropuerto origen, ej: BOG")] string originAirport,
        [Description("Código IATA del aeropuerto destino, ej: AMS")] string destinationAirport,
        [Description("ID del cliente registrado (opcional)")] int? customerId = null,
        [Description("Fecha de embarque o vuelo en formato YYYY-MM-DD (opcional)")] string? flightDate = null)
    {
        var result = await _reservations.CreateReservationAsync(new AwbReservationCreateRequest(
            productType,
            productName,
            quantityKg,
            originAirport,
            destinationAirport,
            customerId,
            flightDate));

        if (!result.Success || result.Reservation is null)
        {
            return Failure(result);
        }

        var reservation = result.Reservation;
        return new
        {
            awbNumber = reservation.AwbNumber,
            shipmentId = reservation.Id,
            status = reservation.Status,
            flightDate = reservation.FlightDate?.ToString("yyyy-MM-dd"),
            temperatureRequiredC = reservation.TemperatureRequiredC,
            phytosanitaryCert = reservation.PhytosanitaryCert,
            message = $"AWB {reservation.AwbNumber} creado exitosamente para {reservation.QuantityKg:0.##}kg de {reservation.ProductName}. Temperatura requerida: {reservation.TemperatureRequiredC:0.#} C."
        };
    }

    [Description("Consulta el estado y detalles de un AWB por su número.")]
    public async Task<object> GetAwbStatusAsync(
        [Description("Número de AWB a consultar, ej: AWB-FLO-20260515-4821")] string awbNumber)
    {
        var result = await _reservations.GetReservationAsync(awbNumber);
        if (!result.Success || result.Reservation is null)
        {
            return new { found = false, errorCode = result.ErrorCode, message = result.ErrorMessage ?? "No se encontro el AWB especificado." };
        }

        var reservation = result.Reservation;
        return new
        {
            found = true,
            reservation.AwbNumber,
            reservation.ProductType,
            reservation.ProductName,
            reservation.QuantityKg,
            reservation.TemperatureRequiredC,
            origin = reservation.OriginAirport,
            destination = reservation.DestinationAirport,
            reservation.Status,
            reservation.PhytosanitaryCert,
            reservation.CreatedAt
        };
    }

    [Description("Actualiza el estado operacional de un AWB existente.")]
    public async Task<object> UpdateAwbStatusAsync(
        [Description("Número de AWB a actualizar, ej: AWB-FLO-20260515-4821")] string awbNumber,
        [Description("Nuevo estado: solicitado, confirmado, en_transito, entregado, rechazado, cancelado")] string status)
    {
        var result = await _reservations.UpdateReservationStatusAsync(new AwbReservationStatusUpdateRequest(awbNumber, status));
        if (!result.Success || result.Reservation is null)
        {
            return Failure(result);
        }

        return new
        {
            success = true,
            found = true,
            result.Reservation.AwbNumber,
            result.Reservation.Status,
            message = $"AWB {result.Reservation.AwbNumber} actualizado a estado {result.Reservation.Status}."
        };
    }

    [Description("Cancela una reserva AWB existente cuando la operacion aun lo permite.")]
    public async Task<object> CancelAwbAsync(
        [Description("Número de AWB a cancelar, ej: AWB-FLO-20260515-4821")] string awbNumber,
        [Description("Motivo de cancelacion (opcional)")] string? reason = null)
    {
        var result = await _reservations.CancelReservationAsync(new AwbReservationCancelRequest(awbNumber, reason));
        if (!result.Success || result.Reservation is null)
        {
            return Failure(result);
        }

        return new
        {
            success = true,
            found = true,
            result.Reservation.AwbNumber,
            result.Reservation.Status,
            message = $"AWB {result.Reservation.AwbNumber} cancelado."
        };
    }

    [Description("Obtiene la temperatura de transporte requerida y recomendaciones para un tipo de producto perecedero.")]
    public static object GetTemperatureRequirements(
        [Description("Tipo de producto: flores, pescado, frutas, mariscos")] string productType)
    {
        var (temp, notes) = productType.ToLowerInvariant() switch
        {
            "flores" => (2.0, "Mantener entre 1°C y 3°C. Evitar exposición a etileno."),
            "pescado" => (-18.0, "Congelado sólido. Cadena de frío ininterrumpida. Max 72h transporte."),
            "mariscos" => (-18.0, "Congelado sólido. Temperatura crítica, sin descongelación parcial."),
            "frutas" => (8.0, "Frutas tropicales. No mezclar con productos de etileno."),
            _ => (2.0, "Consultar requisitos específicos con el operador de carga.")
        };

        return new { productType, temperatureC = temp, recommendations = notes };
    }

    private static object Failure(AwbReservationOperationResult result) => new
    {
        success = false,
        found = !string.Equals(result.ErrorCode, "not_found", StringComparison.OrdinalIgnoreCase),
        errorCode = result.ErrorCode,
        message = result.ErrorMessage ?? "No se pudo completar la operacion AWB."
    };
}

public static class AwbTools
{
    public static AIFunction CreateCreateAwbTool(IAwbReservationGateway reservations)
    {
        var service = new AwbToolService(reservations);
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.CreateAwbAsync))!;
        return AIFunctionFactory.Create(method, service, "create_awb", "Crea un nuevo despacho AWB para carga perecedera.");
    }

    public static AIFunction CreateGetAwbStatusTool(IAwbReservationGateway reservations)
    {
        var service = new AwbToolService(reservations);
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.GetAwbStatusAsync))!;
        return AIFunctionFactory.Create(method, service, "get_awb_status", "Consulta el estado de un AWB por número.");
    }

    public static AIFunction CreateUpdateAwbStatusTool(IAwbReservationGateway reservations)
    {
        var service = new AwbToolService(reservations);
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.UpdateAwbStatusAsync))!;
        return AIFunctionFactory.Create(method, service, "update_awb_status", "Actualiza el estado operacional de un AWB.");
    }

    public static AIFunction CreateCancelAwbTool(IAwbReservationGateway reservations)
    {
        var service = new AwbToolService(reservations);
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.CancelAwbAsync))!;
        return AIFunctionFactory.Create(method, service, "cancel_awb", "Cancela una reserva AWB existente.");
    }

    public static AIFunction CreateGetTemperatureRequirementsTool()
    {
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.GetTemperatureRequirements))!;
        return AIFunctionFactory.Create(method, null, "get_temperature_requirements", "Obtiene temperatura requerida para un producto perecedero.");
    }
}
