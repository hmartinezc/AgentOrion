using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Reflection;
using AgentOrion.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentOrion.Infrastructure.Tools;

public class NotificationToolService
{
    private readonly TursoContext _context;

    public NotificationToolService(TursoContext context)
    {
        _context = context;
    }

    [Description("Simula el envío de una notificación por email relacionada a un AWB o cliente. Guarda el registro pero no envía email real.")]
    public async Task<object> SimulateEmailAsync(
        [Description("Número de AWB relacionado (opcional)")] string? awbNumber = null,
        [Description("Email del destinatario")] string? recipientEmail = null,
        [Description("Asunto del correo")] string? subject = null,
        [Description("Cuerpo del mensaje")] string? body = null)
    {
        using var connection = _context.CreateConnection();
        int? shipmentId = null;
        if (!string.IsNullOrWhiteSpace(awbNumber))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Shipments WHERE AwbNumber = @awb;";
            cmd.Parameters.AddWithValue("@awb", awbNumber);
            var result = await cmd.ExecuteScalarAsync();
            if (result != null) shipmentId = Convert.ToInt32(result);
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = @"
            INSERT INTO SimulatedEmails (ShipmentId, RecipientEmail, Subject, Body, SentAt, Status)
            VALUES (@shipId, @email, @subject, @body, @sentAt, @status);
            SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("@shipId", shipmentId ?? (object)DBNull.Value);
        insert.Parameters.AddWithValue("@email", recipientEmail ?? (object)DBNull.Value);
        insert.Parameters.AddWithValue("@subject", subject ?? (object)DBNull.Value);
        insert.Parameters.AddWithValue("@body", body ?? (object)DBNull.Value);
        insert.Parameters.AddWithValue("@sentAt", DateTime.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("@status", "simulated");
        var idObj = await insert.ExecuteScalarAsync();
        var id = Convert.ToInt32(idObj);

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
    public static AIFunction CreateSimulateEmailTool(TursoContext context)
    {
        var service = new NotificationToolService(context);
        var method = typeof(NotificationToolService).GetMethod(nameof(NotificationToolService.SimulateEmailAsync))!;
        return AIFunctionFactory.Create(method, service, "simulate_email", "Simula el envío de un email de notificación.");
    }
}
