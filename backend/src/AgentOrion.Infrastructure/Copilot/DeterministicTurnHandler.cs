using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOrion.Core.Models;
using AgentOrion.Core.Operations;
using AgentOrion.Core.Persistence;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class DeterministicTurnResult
{
    public required string Content { get; init; }
    public required string RouteName { get; init; }
    public required string RouteDisplayName { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();
}

public static class DeterministicTurnHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex KgRegex = new(@"\b(\d+(?:[\.,]\d+)?)\s*kg\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex SpanishDateRegex = new(@"\b(\d{1,2})\s+de\s+([a-z]+)\s+de\s+(\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AirportRegex = new(@"\b[A-Z]{3}\b", RegexOptions.Compiled);

    public static async Task<DeterministicTurnResult?> TryHandleAsync(
        string prompt,
        ConversationMemoryState? memory,
        ConversationMemoryService memoryService,
        ICustomerRepository customers,
        IAwbReservationGateway awbReservations,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var trimmed = prompt.Trim();
        var normalized = NormalizeText(trimmed);

        if (IsSimpleGreeting(normalized))
        {
            return new DeterministicTurnResult
            {
                RouteName = "operations-general",
                RouteDisplayName = "Triage Operativo",
                Content = "Hola, soy Orion, tu copilot agent para AWB, clientes y cadena de frio. Dime si quieres crear una reserva, registrar un cliente o consultar un AWB."
            };
        }

        if (IsReservationFollowUp(normalized, memory))
        {
            return new DeterministicTurnResult
            {
                RouteName = "awb-dispatch",
                RouteDisplayName = "Despacho AWB",
                Content = BuildReservationSummary(memory)
            };
        }

        if (!IsReservationIntent(normalized) && !HasReservationContext(memory, normalized))
        {
            return null;
        }

        var promptShipment = ParseShipmentDraft(trimmed, normalized);
        ApplyShipment(memory, promptShipment);

        if (!TryExtractCustomerQuery(trimmed, normalized, out var customerQuery))
        {
            var contextCustomer = await ResolveCustomerFromMemoryAsync(memory, customers);
            var contextShipment = MergeShipmentDraft(promptShipment, memory?.Shipment);
            if (contextCustomer is not null)
            {
                return contextShipment.IsComplete
                    ? await CreateReservationAsync(contextCustomer, contextShipment, memory, memoryService, awbReservations, includeSearchTool: false, ct)
                    : new DeterministicTurnResult
                    {
                        RouteName = "awb-dispatch",
                        RouteDisplayName = "Despacho AWB",
                        Content = BuildMissingShipmentDataMessage(contextCustomer)
                    };
            }

            return new DeterministicTurnResult
            {
                RouteName = "awb-dispatch",
                RouteDisplayName = "Despacho AWB",
                Content = BuildMissingReservationStartMessage()
            };
        }

        var matches = (await customers.SearchAsync(customerQuery)).ToList();
        if (matches.Count == 0)
        {
            return new DeterministicTurnResult
            {
                RouteName = "awb-dispatch",
                RouteDisplayName = "Despacho AWB",
                Tools = ["search_customer"],
                Content = $"No encontre un cliente que coincida con \"{customerQuery}\". Puedes registrarlo primero o darme un nombre/email mas exacto."
            };
        }

        var customer = matches[0];
        ApplyCustomer(memory, customer);

        var searchPayload = JsonSerializer.Serialize(new
        {
            count = matches.Count,
            customers = matches.Select(c => new { c.Id, c.FullName, c.Email, c.Phone, c.CompanyName, c.Country, c.Address, c.DocumentNumber })
        }, JsonOptions);

        if (memory is not null)
        {
            memoryService.ApplyToolResult(memory, "search_customer", searchPayload);
        }

        var shipment = MergeShipmentDraft(promptShipment, memory?.Shipment);
        if (!shipment.IsComplete)
        {
            return new DeterministicTurnResult
            {
                RouteName = "awb-dispatch",
                RouteDisplayName = "Despacho AWB",
                Tools = ["search_customer"],
                Content = BuildMissingShipmentDataMessage(customer)
            };
        }

        return await CreateReservationAsync(customer, shipment, memory, memoryService, awbReservations, includeSearchTool: true, ct);
    }

    private static async Task<DeterministicTurnResult> CreateReservationAsync(
        Customer customer,
        ShipmentDraft shipment,
        ConversationMemoryState? memory,
        ConversationMemoryService memoryService,
        IAwbReservationGateway awbReservations,
        bool includeSearchTool,
        CancellationToken ct)
    {
        int? customerId = customer.Id > 0 ? customer.Id : null;
        var createResult = await awbReservations.CreateReservationAsync(new AwbReservationCreateRequest(
            shipment.ProductType!,
            shipment.ProductName!,
            shipment.QuantityKg!.Value,
            shipment.OriginAirport!,
            shipment.DestinationAirport!,
            customerId,
            shipment.FlightDate), ct);

        if (!createResult.Success || createResult.Reservation is null)
        {
            return new DeterministicTurnResult
            {
                RouteName = "awb-dispatch",
                RouteDisplayName = "Despacho AWB",
                Tools = includeSearchTool ? ["search_customer", "create_awb"] : ["create_awb"],
                Content = createResult.ErrorMessage ?? "No se pudo crear la reserva AWB."
            };
        }

        var reservation = createResult.Reservation;
        var createPayload = JsonSerializer.Serialize(new
        {
            awbNumber = reservation.AwbNumber,
            status = reservation.Status,
            flightDate = reservation.FlightDate?.ToString("yyyy-MM-dd"),
            temperatureRequiredC = reservation.TemperatureRequiredC
        }, JsonOptions);

        if (memory is not null)
        {
            memoryService.ApplyToolResult(memory, "create_awb", createPayload);
            memory.Shipment.ProductType = shipment.ProductType;
            memory.Shipment.ProductName = shipment.ProductName;
            memory.Shipment.QuantityKg = shipment.QuantityKg;
            memory.Shipment.OriginAirport = shipment.OriginAirport;
            memory.Shipment.DestinationAirport = shipment.DestinationAirport;
            if (DateTime.TryParse(shipment.FlightDate, out var parsedFlightDate))
            {
                memory.Shipment.FlightDate = parsedFlightDate;
            }
        }

        var awbNumber = reservation.AwbNumber ?? "n/d";
        var temperature = reservation.TemperatureRequiredC?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/d";

        return new DeterministicTurnResult
        {
            RouteName = "awb-dispatch",
            RouteDisplayName = "Despacho AWB",
            Tools = includeSearchTool ? ["search_customer", "create_awb"] : ["create_awb"],
            Content = $"Reserva AWB creada para {customer.FullName}.\n\n- AWB: {awbNumber}\n- Producto: {shipment.ProductName} ({shipment.QuantityKg:0.##} kg)\n- Ruta: {shipment.OriginAirport} -> {shipment.DestinationAirport}\n- Fecha de vuelo: {shipment.FlightDate ?? "n/d"}\n- Temperatura requerida: {temperature} C\n- Estado: solicitado"
        };
    }

    private static bool IsSimpleGreeting(string normalized)
    {
        var cleaned = Regex.Replace(normalized, @"^[\p{P}\s]+|[\p{P}\s]+$", string.Empty);
        return cleaned is "hola" or "buenas" or "buenos dias" or "buenas tardes" or "buenas noches" or "hello" or "hi" or "hey";
    }

    private static bool IsReservationIntent(string normalized) =>
        normalized.Contains("reserva") || normalized.Contains("booking") || normalized.Contains("awb") || normalized.Contains("despacho");

    private static bool HasReservationContext(ConversationMemoryState? memory, string normalized) =>
        string.Equals(memory?.CurrentIntent, "awb_creation", StringComparison.OrdinalIgnoreCase) && HasShipmentSignal(normalized);

    private static bool HasShipmentSignal(string normalized) =>
        normalized.Contains("producto") ||
        normalized.Contains("ruta") ||
        normalized.Contains("origen") ||
        normalized.Contains("destino") ||
        KgRegex.IsMatch(normalized) ||
        DateRegex.IsMatch(normalized) ||
        SpanishDateRegex.IsMatch(normalized);

    private static bool IsReservationFollowUp(string normalized, ConversationMemoryState? memory)
    {
        if (memory is null || !string.Equals(memory.CurrentIntent, "awb_creation", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalized.Contains("se creo") ||
            normalized.Contains("esta creada") ||
            normalized.Contains("quedo creada") ||
            normalized.Contains("cual fue el awb") ||
            normalized.Contains("cual es el awb") ||
            normalized.Contains("numero de awb") ||
            normalized.Contains("reserva actual") ||
            normalized.Contains("muestrame la reserva") ||
            normalized.Contains("muestre la reserva") ||
            normalized.Contains("detalle de la reserva");
    }

    private static string BuildReservationSummary(ConversationMemoryState? memory)
    {
        var shipment = memory?.Shipment;
        if (shipment is null || string.IsNullOrWhiteSpace(shipment.AwbNumber))
        {
            return "Aun no tengo una reserva AWB creada en esta conversacion. Dame producto, peso, origen, destino y fecha para crearla.";
        }

        return "Si, la reserva AWB esta creada.\n\n" +
            $"- AWB: {shipment.AwbNumber}\n" +
            $"- Cliente: {memory?.Customer.FullName ?? "n/d"}\n" +
            $"- Producto: {shipment.ProductName ?? shipment.ProductType ?? "n/d"}{FormatWeight(shipment.QuantityKg)}\n" +
            $"- Ruta: {shipment.OriginAirport ?? "n/d"} -> {shipment.DestinationAirport ?? "n/d"}\n" +
            $"- Fecha de vuelo: {shipment.FlightDate?.ToString("yyyy-MM-dd") ?? "n/d"}\n" +
            $"- Temperatura requerida: {shipment.TemperatureRequiredC?.ToString("0.#", CultureInfo.InvariantCulture) ?? "n/d"} C\n" +
            $"- Estado: {shipment.Status ?? "n/d"}";
    }

    private static string FormatWeight(double? quantityKg) => quantityKg.HasValue
        ? $" ({quantityKg.Value:0.##} kg)"
        : string.Empty;

    private static bool TryExtractCustomerQuery(string originalPrompt, string normalizedPrompt, out string query)
    {
        var markers = new[] { "cliente", "customer", "empresa" };

        foreach (var marker in markers)
        {
            var index = normalizedPrompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var tail = originalPrompt[(index + marker.Length)..].Trim(' ', ':', '.', ',', ';');
            if (tail.StartsWith("de ", StringComparison.OrdinalIgnoreCase)) tail = tail[3..].Trim();
            if (tail.StartsWith("para ", StringComparison.OrdinalIgnoreCase)) tail = tail[5..].Trim();
            if (tail.StartsWith("el ", StringComparison.OrdinalIgnoreCase)) tail = tail[3..].Trim();
            if (tail.StartsWith("la ", StringComparison.OrdinalIgnoreCase)) tail = tail[3..].Trim();

            var stopAt = FindCustomerTailStopIndex(tail);
            query = (stopAt > 0 ? tail[..stopAt] : tail).Trim();
            return !string.IsNullOrWhiteSpace(query);
        }

        query = string.Empty;
        return false;
    }

    private static int FindCustomerTailStopIndex(string tail)
    {
        var normalizedTail = NormalizeText(tail);
        var stopAt = tail.IndexOfAny(['.', ',', ';']);
        var markers = new[] { " producto", " ruta", " flores", " pescado", " mariscos", " frutas", " fruta", " origen", " destino", " fecha", " vuelo", " kg" };

        foreach (var marker in markers)
        {
            var index = normalizedTail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (stopAt < 0 || index < stopAt))
            {
                stopAt = index;
            }
        }

        var kgMatch = KgRegex.Match(tail);
        if (kgMatch.Success && (stopAt < 0 || kgMatch.Index < stopAt))
        {
            stopAt = kgMatch.Index;
        }

        return stopAt;
    }

    private static ShipmentDraft ParseShipmentDraft(string originalPrompt, string normalizedPrompt)
    {
        var productType = normalizedPrompt switch
        {
            var text when text.Contains("cangrejo") => "mariscos",
            var text when text.Contains("camaron") || text.Contains("camarón") => "mariscos",
            var text when text.Contains("langosta") => "mariscos",
            var text when text.Contains("mariscos") => "mariscos",
            var text when text.Contains("flores") => "flores",
            var text when text.Contains("pescado") => "pescado",
            var text when text.Contains("mariscos") => "mariscos",
            var text when text.Contains("frutas") || text.Contains("fruta") => "frutas",
            _ => null
        };

        var productName = normalizedPrompt switch
        {
            var text when text.Contains("cangrejo") => "Cangrejo",
            var text when text.Contains("camaron") || text.Contains("camarón") => "Camarón",
            var text when text.Contains("langosta") => "Langosta",
            var text when text.Contains("rosas") => "Rosas",
            var text when text.Contains("tilapia") => "Tilapia",
            var text when text.Contains("banano") => "Banano",
            _ => productType
        };

        var (routeOrigin, routeDestination) = ExtractRouteAirports(originalPrompt);

        return new ShipmentDraft
        {
            ProductType = productType,
            ProductName = productName,
            QuantityKg = ParseKg(originalPrompt),
            OriginAirport = ExtractAirportAfter(normalizedPrompt, originalPrompt, "origen") ?? routeOrigin,
            DestinationAirport = ExtractAirportAfter(normalizedPrompt, originalPrompt, "destino") ?? routeDestination,
            FlightDate = ParseFlightDate(originalPrompt)
        };
    }

    private static ShipmentDraft MergeShipmentDraft(ShipmentDraft promptDraft, ConversationShipmentMemory? memoryShipment)
    {
        if (memoryShipment is null)
        {
            return promptDraft;
        }

        return new ShipmentDraft
        {
            ProductType = promptDraft.ProductType ?? memoryShipment.ProductType,
            ProductName = promptDraft.ProductName ?? memoryShipment.ProductName,
            QuantityKg = promptDraft.QuantityKg ?? memoryShipment.QuantityKg,
            OriginAirport = promptDraft.OriginAirport ?? memoryShipment.OriginAirport,
            DestinationAirport = promptDraft.DestinationAirport ?? memoryShipment.DestinationAirport,
            FlightDate = promptDraft.FlightDate ?? memoryShipment.FlightDate?.ToString("yyyy-MM-dd")
        };
    }

    private static void ApplyShipment(ConversationMemoryState? memory, ShipmentDraft shipment)
    {
        if (memory is null)
        {
            return;
        }

        memory.Shipment.ProductType ??= shipment.ProductType;
        memory.Shipment.ProductName ??= shipment.ProductName;
        memory.Shipment.QuantityKg ??= shipment.QuantityKg;
        memory.Shipment.OriginAirport ??= shipment.OriginAirport;
        memory.Shipment.DestinationAirport ??= shipment.DestinationAirport;
        if (!memory.Shipment.FlightDate.HasValue && DateTime.TryParse(shipment.FlightDate, out var parsedFlightDate))
        {
            memory.Shipment.FlightDate = parsedFlightDate;
        }

        memory.UpdatedAt = DateTime.UtcNow;
    }

    private static async Task<Customer?> ResolveCustomerFromMemoryAsync(ConversationMemoryState? memory, ICustomerRepository customers)
    {
        if (memory?.Customer.CustomerId is int customerId)
        {
            var customerById = await customers.GetByIdAsync(customerId);
            if (customerById is not null)
            {
                return customerById;
            }
        }

        if (!string.IsNullOrWhiteSpace(memory?.Customer.FullName))
        {
            var matches = (await customers.SearchAsync(memory.Customer.FullName)).ToList();
            return matches.FirstOrDefault() ?? new Customer
            {
                Id = memory.Customer.CustomerId ?? 0,
                FullName = memory.Customer.FullName,
                Email = memory.Customer.Email,
                Phone = memory.Customer.Phone,
                CompanyName = memory.Customer.CompanyName,
                Country = memory.Customer.Country,
                Address = memory.Customer.Address,
                DocumentNumber = memory.Customer.DocumentNumber
            };
        }

        return null;
    }

    private static double? ParseKg(string prompt)
    {
        var match = KgRegex.Match(prompt);
        if (!match.Success)
        {
            return null;
        }

        var normalized = match.Groups[1].Value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? ExtractAirportAfter(string normalizedPrompt, string originalPrompt, string marker)
    {
        var index = normalizedPrompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var segment = originalPrompt[index..];
        var match = AirportRegex.Match(segment.ToUpperInvariant());
        return match.Success ? match.Value : null;
    }

    private static (string? Origin, string? Destination) ExtractRouteAirports(string originalPrompt)
    {
        var normalized = NormalizeText(originalPrompt);
        var routeIndex = normalized.IndexOf("ruta", StringComparison.OrdinalIgnoreCase);
        if (routeIndex < 0)
        {
            return (null, null);
        }

        var airports = AirportRegex.Matches(originalPrompt[routeIndex..].ToUpperInvariant())
            .Select(match => match.Value)
            .Where(value => value != "AWB")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        return airports.Length >= 2
            ? (airports[0], airports[1])
            : (null, null);
    }

    private static string? ParseFlightDate(string originalPrompt)
    {
        var isoMatch = DateRegex.Match(originalPrompt);
        if (isoMatch.Success)
        {
            return isoMatch.Value;
        }

        var normalized = NormalizeText(originalPrompt);
        var spanishMatch = SpanishDateRegex.Match(normalized);
        if (!spanishMatch.Success ||
            !int.TryParse(spanishMatch.Groups[1].Value, out var day) ||
            !int.TryParse(spanishMatch.Groups[3].Value, out var year))
        {
            return null;
        }

        var month = MonthNumber(spanishMatch.Groups[2].Value);
        if (!month.HasValue)
        {
            return null;
        }

        try
        {
            return new DateTime(year, month.Value, day).ToString("yyyy-MM-dd");
        }
        catch
        {
            return null;
        }
    }

    private static int? MonthNumber(string month) => month.ToLowerInvariant() switch
    {
        "enero" => 1,
        "febrero" => 2,
        "marzo" => 3,
        "abril" => 4,
        "mayo" => 5,
        "junio" => 6,
        "julio" => 7,
        "agosto" => 8,
        "septiembre" => 9,
        "setiembre" => 9,
        "octubre" => 10,
        "noviembre" => 11,
        "diciembre" => 12,
        _ => null
    };

    private static void ApplyCustomer(ConversationMemoryState? memory, Customer customer)
    {
        if (memory is null)
        {
            return;
        }

        memory.Customer.CustomerId = customer.Id;
        memory.Customer.FullName = customer.FullName;
        memory.Customer.Email = customer.Email;
        memory.Customer.Phone = customer.Phone;
        memory.Customer.CompanyName = customer.CompanyName;
        memory.Customer.Country = customer.Country;
        memory.Customer.Address = customer.Address;
        memory.Customer.DocumentNumber = customer.DocumentNumber;
        memory.CurrentIntent = "awb_creation";
        memory.UpdatedAt = DateTime.UtcNow;
    }

    private static string BuildMissingShipmentDataMessage(Customer customer) =>
        $"Encontre al cliente {customer.FullName} (ID: {customer.Id}). Para crear la reserva AWB necesito estos datos:\n\n" +
        "- Tipo de producto: flores, pescado, frutas o mariscos\n" +
        "- Nombre especifico del producto, por ejemplo Rosas o Tilapia\n" +
        "- Peso en kg\n" +
        "- Aeropuerto de origen, codigo IATA\n" +
        "- Aeropuerto de destino, codigo IATA\n" +
        "- Fecha de vuelo opcional en formato YYYY-MM-DD";

    private static string BuildMissingReservationStartMessage() =>
        "Para crear una reserva AWB necesito estos datos:\n\n" +
        "- Cliente registrado o nombre/email para buscarlo\n" +
        "- Tipo de producto: flores, pescado, frutas o mariscos\n" +
        "- Nombre especifico del producto\n" +
        "- Peso en kg\n" +
        "- Aeropuerto de origen, codigo IATA\n" +
        "- Aeropuerto de destino, codigo IATA\n" +
        "- Fecha de vuelo opcional en formato YYYY-MM-DD";

    private static string NormalizeText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed class ShipmentDraft
    {
        public string? ProductType { get; init; }
        public string? ProductName { get; init; }
        public double? QuantityKg { get; init; }
        public string? OriginAirport { get; init; }
        public string? DestinationAirport { get; init; }
        public string? FlightDate { get; init; }

        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(ProductType) &&
            !string.IsNullOrWhiteSpace(ProductName) &&
            QuantityKg.HasValue &&
            !string.IsNullOrWhiteSpace(OriginAirport) &&
            !string.IsNullOrWhiteSpace(DestinationAirport);
    }
}
