using System.Text.Json;
using System.Text.RegularExpressions;
using AgentOrion.Core.Models;

namespace AgentOrion.Infrastructure.Copilot;

public sealed class ConversationMemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"\+?\d[\d\s-]{6,}", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex SpanishDateRegex = new(@"\b(\d{1,2})\s+de\s+([a-z]+)\s+de\s+(\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex KgRegex = new(@"\b(\d+(?:[\.,]\d+)?)\s*kg\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AirportRegex = new(@"\b[A-Z]{3}\b", RegexOptions.Compiled);

    public ConversationMemoryState Create(string sessionId) => new() { SessionId = sessionId };

    public void ApplyPrompt(ConversationMemoryState memory, string prompt)
    {
        var normalizedPrompt = prompt.Trim();
        var lower = normalizedPrompt.ToLowerInvariant();

        memory.CurrentIntent = InferIntent(lower);
        memory.Customer.Email ??= MatchValue(EmailRegex, normalizedPrompt);
        memory.Customer.Phone ??= MatchValue(PhoneRegex, normalizedPrompt);
        memory.Shipment.FlightDate ??= ParseDate(normalizedPrompt);
        memory.Shipment.QuantityKg ??= ParseDouble(normalizedPrompt, KgRegex);

        if (lower.Contains("direccion") || lower.Contains("dirección"))
        {
            memory.Customer.Address ??= ExtractTail(normalizedPrompt, "direccion", "dirección");
        }

        if (lower.Contains("documento") || lower.Contains("nit") || lower.Contains("identificacion") || lower.Contains("identificación"))
        {
            memory.Customer.DocumentNumber ??= ExtractTail(normalizedPrompt, "documento", "nit", "identificacion", "identificación");
        }

        if (lower.Contains("se llama") || lower.Contains("llamado") || lower.Contains("cliente nuevo") || lower.Contains("cliente "))
        {
            memory.Customer.FullName ??= ExtractCustomerName(normalizedPrompt);
        }

        if (lower.Contains("empresa"))
        {
            memory.Customer.CompanyName ??= ExtractTail(normalizedPrompt, "empresa");
        }

        if (memory.Customer.CompanyName is null && memory.Customer.FullName is not null && lower.Contains("cliente"))
        {
            memory.Customer.CompanyName = memory.Customer.FullName;
        }

        var (productType, productName) = InferProduct(lower);
        memory.Shipment.ProductType ??= productType;
        memory.Shipment.ProductName ??= productName;

        if (lower.Contains("origen"))
        {
            memory.Shipment.OriginAirport ??= ExtractAirportAfter(lower, normalizedPrompt, "origen");
        }

        if (lower.Contains("destino"))
        {
            memory.Shipment.DestinationAirport ??= ExtractAirportAfter(lower, normalizedPrompt, "destino");
        }

        var (routeOrigin, routeDestination) = ExtractRouteAirports(normalizedPrompt);
        memory.Shipment.OriginAirport ??= routeOrigin;
        memory.Shipment.DestinationAirport ??= routeDestination;

        memory.UpdatedAt = DateTime.UtcNow;
    }

    public void ApplyToolResult(ConversationMemoryState memory, string toolName, string? resultPayload)
    {
        if (string.IsNullOrWhiteSpace(resultPayload))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(resultPayload);
            var root = document.RootElement;

            switch (toolName)
            {
                case "register_customer":
                    if (root.TryGetProperty("customerId", out var customerId)) memory.Customer.CustomerId = customerId.GetInt32();
                    if (root.TryGetProperty("fullName", out var fullName)) memory.Customer.FullName = fullName.GetString() ?? memory.Customer.FullName;
                    if (root.TryGetProperty("companyName", out var companyName)) memory.Customer.CompanyName = companyName.GetString() ?? memory.Customer.CompanyName;
                    break;

                case "search_customer":
                    if (root.TryGetProperty("count", out var count) && count.GetInt32() > 0 && root.TryGetProperty("customers", out var customers) && customers.ValueKind == JsonValueKind.Array)
                    {
                        var first = customers.EnumerateArray().FirstOrDefault();
                        if (first.TryGetProperty("id", out var id)) memory.Customer.CustomerId = id.GetInt32();
                        if (first.TryGetProperty("fullName", out var name)) memory.Customer.FullName = name.GetString() ?? memory.Customer.FullName;
                        if (first.TryGetProperty("email", out var email)) memory.Customer.Email = email.GetString() ?? memory.Customer.Email;
                        if (first.TryGetProperty("phone", out var phone)) memory.Customer.Phone = phone.GetString() ?? memory.Customer.Phone;
                        if (first.TryGetProperty("companyName", out var company)) memory.Customer.CompanyName = company.GetString() ?? memory.Customer.CompanyName;
                        if (first.TryGetProperty("country", out var country)) memory.Customer.Country = country.GetString() ?? memory.Customer.Country;
                    }
                    break;

                case "create_awb":
                    if (root.TryGetProperty("awbNumber", out var awbNumber)) memory.Shipment.AwbNumber = awbNumber.GetString() ?? memory.Shipment.AwbNumber;
                    if (root.TryGetProperty("status", out var status)) memory.Shipment.Status = status.GetString() ?? memory.Shipment.Status;
                    if (root.TryGetProperty("flightDate", out var flightDate) && flightDate.ValueKind == JsonValueKind.String && DateTime.TryParse(flightDate.GetString(), out var parsedFlightDate)) memory.Shipment.FlightDate = parsedFlightDate;
                    if (root.TryGetProperty("temperatureRequiredC", out var temp) && temp.ValueKind == JsonValueKind.Number) memory.Shipment.TemperatureRequiredC = temp.GetDouble();
                    break;

                case "get_awb_status":
                    if (root.TryGetProperty("AwbNumber", out var awb)) memory.Shipment.AwbNumber = awb.GetString() ?? memory.Shipment.AwbNumber;
                    if (root.TryGetProperty("Status", out var awbStatus)) memory.Shipment.Status = awbStatus.GetString() ?? memory.Shipment.Status;
                    break;

                case "update_awb_status":
                case "cancel_awb":
                    if (TryGetString(root, "AwbNumber", "awbNumber") is { } updatedAwb) memory.Shipment.AwbNumber = updatedAwb;
                    if (TryGetString(root, "Status", "status") is { } updatedStatus) memory.Shipment.Status = updatedStatus;
                    break;

                case "get_temperature_requirements":
                    if (root.TryGetProperty("temperatureC", out var reqTemp) && reqTemp.ValueKind == JsonValueKind.Number) memory.Shipment.TemperatureRequiredC = reqTemp.GetDouble();
                    break;
            }
        }
        catch
        {
            // Best effort.
        }

        memory.UpdatedAt = DateTime.UtcNow;
    }

    public string BuildContextAppendix(ConversationMemoryState? memory)
    {
        if (memory is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(memory.Customer.FullName) || !string.IsNullOrWhiteSpace(memory.Customer.Email) || memory.Customer.CustomerId.HasValue)
        {
            parts.Add($"CLIENTE EN CONTEXTO: id={memory.Customer.CustomerId?.ToString() ?? "n/d"}, nombre={memory.Customer.FullName ?? "n/d"}, empresa={memory.Customer.CompanyName ?? "n/d"}, email={memory.Customer.Email ?? "n/d"}, telefono={memory.Customer.Phone ?? "n/d"}, direccion={memory.Customer.Address ?? "n/d"}, documento={memory.Customer.DocumentNumber ?? "n/d"}.");
        }

        if (!string.IsNullOrWhiteSpace(memory.Shipment.ProductType) || !string.IsNullOrWhiteSpace(memory.Shipment.AwbNumber) || memory.Shipment.QuantityKg.HasValue)
        {
            parts.Add($"ENVIO EN CONTEXTO: awb={memory.Shipment.AwbNumber ?? "n/d"}, producto={memory.Shipment.ProductType ?? "n/d"}, nombreProducto={memory.Shipment.ProductName ?? "n/d"}, pesoKg={memory.Shipment.QuantityKg?.ToString() ?? "n/d"}, origen={memory.Shipment.OriginAirport ?? "n/d"}, destino={memory.Shipment.DestinationAirport ?? "n/d"}, fechaVuelo={memory.Shipment.FlightDate?.ToString("yyyy-MM-dd") ?? "n/d"}, temperatura={memory.Shipment.TemperatureRequiredC?.ToString() ?? "n/d"}, estado={memory.Shipment.Status ?? "n/d"}.");
        }

        return parts.Count == 0
            ? string.Empty
            : "\n\nMEMORIA ESTRUCTURADA DE LA CONVERSACION:\n- " + string.Join("\n- ", parts);
    }

    private static string InferIntent(string lower)
    {
        if (lower.Contains("registr") || lower.Contains("crear cliente") || lower.Contains("nuevo cliente")) return "customer_registration";
        if (lower.Contains("awb") || lower.Contains("reserva") || lower.Contains("booking")) return "awb_creation";
        if (lower.Contains("producto") || lower.Contains("ruta") || lower.Contains("origen") || lower.Contains("destino") || KgRegex.IsMatch(lower)) return "awb_creation";
        if (lower.Contains("temperatura") || lower.Contains("cadena de frio") || lower.Contains("cadena de frío")) return "cold_chain";
        if (lower.Contains("correo") || lower.Contains("email") || lower.Contains("notific")) return "client_communication";
        return "general";
    }

    private static (string? ProductType, string? ProductName) InferProduct(string lower) => lower switch
    {
        var text when text.Contains("cangrejo") => ("mariscos", "Cangrejo"),
        var text when text.Contains("camaron") || text.Contains("camarón") => ("mariscos", "Camarón"),
        var text when text.Contains("langosta") => ("mariscos", "Langosta"),
        var text when text.Contains("mariscos") => ("mariscos", null),
        var text when text.Contains("tilapia") => ("pescado", "Tilapia"),
        var text when text.Contains("salmon") || text.Contains("salmón") => ("pescado", "Salmón"),
        var text when text.Contains("pescado") => ("pescado", null),
        var text when text.Contains("rosas") => ("flores", "Rosas"),
        var text when text.Contains("flores") => ("flores", null),
        var text when text.Contains("banano") => ("frutas", "Banano"),
        var text when text.Contains("frutas") || text.Contains("fruta") => ("frutas", null),
        _ => (null, null)
    };

    private static string? MatchValue(Regex regex, string text)
    {
        var match = regex.Match(text);
        return match.Success ? match.Value.Trim() : null;
    }

    private static string? TryGetString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static DateTime? ParseDate(string text)
    {
        var match = DateRegex.Match(text);
        if (match.Success && DateTime.TryParse(match.Value, out var parsed))
        {
            return parsed;
        }

        var normalized = text.ToLowerInvariant();
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
            return new DateTime(year, month.Value, day);
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

    private static double? ParseDouble(string text, Regex regex)
    {
        var match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var normalized = match.Groups[1].Value.Replace(',', '.');
        return double.TryParse(normalized, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? ExtractTail(string text, params string[] anchors)
    {
        foreach (var anchor in anchors)
        {
            var index = text.ToLowerInvariant().IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var tail = text[(index + anchor.Length)..].Trim(' ', ':', '.', ',');
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    return tail;
                }
            }
        }

        return null;
    }

    private static string? ExtractCustomerName(string text)
    {
        var markers = new[] { "se llama", "llamado", "llamada", "cliente nuevo llamado", "cliente llamado" };
        foreach (var marker in markers)
        {
            var index = text.ToLowerInvariant().IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var tail = text[(index + marker.Length)..].Trim(' ', ':', '.', ',');
                var stopAt = tail.IndexOfAny([',', '.']);
                return stopAt > 0 ? tail[..stopAt].Trim() : tail;
            }
        }

        return null;
    }

    private static string? ExtractAirportAfter(string lower, string original, string marker)
    {
        var index = lower.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var segment = original[index..];
        var match = AirportRegex.Match(segment.ToUpperInvariant());
        return match.Success ? match.Value : null;
    }

    private static (string? Origin, string? Destination) ExtractRouteAirports(string original)
    {
        var lower = original.ToLowerInvariant();
        var routeIndex = lower.IndexOf("ruta", StringComparison.OrdinalIgnoreCase);
        if (routeIndex < 0)
        {
            return (null, null);
        }

        var airports = AirportRegex.Matches(original[routeIndex..].ToUpperInvariant())
            .Select(match => match.Value)
            .Where(value => value != "AWB")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        return airports.Length >= 2
            ? (airports[0], airports[1])
            : (null, null);
    }
}
