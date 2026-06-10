using System.Text;
using System.Text.Json;
using AgentOrion.Core.Persistence;
using AgentOrion.Infrastructure.Copilot;

namespace AgentOrion.Api.Endpoints;

public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/reset", async (
            ResetChatRequest request,
            AgentFactory agentFactory,
            IConversationMemoryRepository memoryRepository,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                await agentFactory.DeleteSessionAsync(request.SessionId, ct);
                await memoryRepository.DeleteAsync(request.SessionId);
            }

            return Results.Ok();
        });

        app.MapPost("/api/chat", async (
            ChatRequest request,
            ChatTurnService chatTurns,
            CancellationToken ct,
            HttpContext httpContext) =>
        {
            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.Append("Cache-Control", "no-cache");
            httpContext.Response.Headers.Append("Connection", "keep-alive");

            await foreach (var streamEvent in chatTurns.ProcessAsync(
                               new ChatTurnRequest(request.Message, request.SessionId, request.Mode),
                               ct))
            {
                await WriteSseAsync(httpContext, streamEvent, ct);
            }

            return Results.Empty;
        });

        return app;
    }

    private static async Task WriteSseAsync(HttpContext httpContext, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {SerializeSse(payload)}\n\n");
        await httpContext.Response.Body.WriteAsync(bytes, ct);
        await httpContext.Response.Body.FlushAsync(ct);
    }

    private static string SerializeSse(object payload) => JsonSerializer.Serialize(payload, SseJsonOptions);
}

public record ChatRequest(string Message, string? SessionId = null, string? Mode = null);
public record ResetChatRequest(string SessionId);
