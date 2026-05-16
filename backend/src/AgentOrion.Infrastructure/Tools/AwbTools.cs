using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;

namespace AgentOrion.Infrastructure.Tools;

public class AwbToolService
{
    private readonly IShipmentRepository _shipments;

    public AwbToolService(IShipmentRepository shipments)
    {
        _shipments = shipments;
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
        double tempReq = productType.ToLowerInvariant() switch
        {
            "flores" => 2.0,
            "pescado" => -18.0,
            "mariscos" => -18.0,
            "frutas" => 8.0,
            _ => 2.0
        };

        var awbNumber = $"AWB-{productType.ToUpperInvariant()[..3]}-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";

        var shipment = new Shipment
        {
            AwbNumber = awbNumber,
            CustomerId = customerId,
            ProductType = productType,
            ProductName = productName,
            QuantityKg = quantityKg,
            TemperatureRequiredC = tempReq,
            OriginAirport = originAirport.ToUpperInvariant(),
            DestinationAirport = destinationAirport.ToUpperInvariant(),
            FlightDate = TryParseFlightDate(flightDate),
            Status = "solicitado",
            PhytosanitaryCert = $"PHYTO-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}"
        };

        var id = await _shipments.CreateAsync(shipment);
        await _shipments.AddEventAsync(id, "awb_created", JsonSerializer.Serialize(new { tempRequired = tempReq, awb = awbNumber }));

        return new
        {
            awbNumber,
            shipmentId = id,
            status = shipment.Status,
            flightDate = shipment.FlightDate?.ToString("yyyy-MM-dd"),
            temperatureRequiredC = tempReq,
            phytosanitaryCert = shipment.PhytosanitaryCert,
            message = $"AWB {awbNumber} creado exitosamente para {quantityKg}kg de {productName}. Temperatura requerida: {tempReq}°C."
        };
    }

    [Description("Consulta el estado y detalles de un AWB por su número.")]
    public async Task<object> GetAwbStatusAsync(
        [Description("Número de AWB a consultar, ej: AWB-FLO-20260515-4821")] string awbNumber)
    {
        var s = await _shipments.GetByAwbAsync(awbNumber);
        if (s == null)
            return new { found = false, message = "No se encontró el AWB especificado." };

        return new
        {
            found = true,
            s.AwbNumber,
            s.ProductType,
            s.ProductName,
            s.QuantityKg,
            s.TemperatureRequiredC,
            origin = s.OriginAirport,
            destination = s.DestinationAirport,
            s.Status,
            s.PhytosanitaryCert,
            s.CreatedAt
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

public static class AwbTools
{
    public static AIFunction CreateCreateAwbTool(IShipmentRepository shipments)
    {
        var service = new AwbToolService(shipments);
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.CreateAwbAsync))!;
        return AIFunctionFactory.Create(method, service, "create_awb", "Crea un nuevo despacho AWB para carga perecedera.");
    }

    public static AIFunction CreateGetAwbStatusTool(IShipmentRepository shipments)
    {
        var service = new AwbToolService(shipments);
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.GetAwbStatusAsync))!;
        return AIFunctionFactory.Create(method, service, "get_awb_status", "Consulta el estado de un AWB por número.");
    }

    public static AIFunction CreateGetTemperatureRequirementsTool()
    {
        var method = typeof(AwbToolService).GetMethod(nameof(AwbToolService.GetTemperatureRequirements))!;
        return AIFunctionFactory.Create(method, null, "get_temperature_requirements", "Obtiene temperatura requerida para un producto perecedero.");
    }
}
