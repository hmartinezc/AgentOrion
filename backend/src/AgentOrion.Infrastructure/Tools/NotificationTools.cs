using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;
using AgentOrion.Core.Models;
using AgentOrion.Core.Persistence;

namespace AgentOrion.Infrastructure.Tools;

public class NotificationToolService
{
    private readonly ISimulatedEmailRepository _emails;
    private readonly IShipmentRepository _shipments;

    public NotificationToolService(ISimulatedEmailRepository emails, IShipmentRepository shipments)
    {
        _emails = emails;
        _shipments = shipments;
    }

    [Description("Simula el envío de una notificación por email relacionada a un AWB o cliente. Guarda el registro pero no envía email real.")]
    public async Task<object> SimulateEmailAsync(
        [Description("Número de AWB relacionado (opcional)")] string? awbNumber = null,
        [Description("Email del destinatario")] string? recipientEmail = null,
        [Description("Asunto del correo")] string? subject = null,
        [Description("Cuerpo del mensaje")] string? body = null)
    {
        int? shipmentId = null;
        if (!string.IsNullOrWhiteSpace(awbNumber))
        {
            shipmentId = (await _shipments.GetByAwbAsync(awbNumber))?.Id;
        }

        var id = await _emails.CreateAsync(new SimulatedEmail
        {
            ShipmentId = shipmentId,
            RecipientEmail = recipientEmail,
            Subject = subject,
            Body = body,
            Status = "simulated"
        });

        return new
        {
            emailId = id,
            simulated = true,
            status = "simulated",
            message = $"Correo simulado registrado (ID {id}). En producción se enviaría a {recipientEmail}."
        };
    }
}

public static class NotificationTools
{
    public static AIFunction CreateSimulateEmailTool(ISimulatedEmailRepository emails, IShipmentRepository shipments)
    {
        var service = new NotificationToolService(emails, shipments);
        var method = typeof(NotificationToolService).GetMethod(nameof(NotificationToolService.SimulateEmailAsync))!;
        return AIFunctionFactory.Create(method, service, "simulate_email", "Simula el envío de un email de notificación.");
    }
}
