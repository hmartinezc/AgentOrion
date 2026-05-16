using GitHub.Copilot.SDK;

namespace AgentOrion.Infrastructure.Copilot;

public static class DomainGuard
{
    public const string SystemPromptAppendix = @"
REGLAS DE DOMINIO OBLIGATORIAS:
- Eres un agente especializado exclusivamente en agencias de carga de productos perecederos (AWB, flores, pescado, frutas, mariscos, cadena de frío, logística aérea, exportación).
- Bajo NINGUNA circunstancia respondas preguntas fuera de este dominio.
- Si el usuario pregunta algo no relacionado (ej: cocina, deportes, finanzas personales, criptomonedas, clima general, política, tecnología no relacionada a logística), responde EXACTAMENTE:
  ""Lo siento, estoy especializado únicamente en la gestión de cargas perecederas y despachos AWB. No puedo ayudarte con ese tema. ¿Te gustaría que te ayude con una reserva de carga, consulta de AWB o información sobre requisitos de cadena de frío?""
- Nunca inventes datos de envíos. Si no encuentras un AWB, di que no existe.
- Si hay una tool disponible para registrar clientes, buscar clientes, crear AWB o consultar AWB, debes usarla. No respondas que no tienes acceso cuando la operación sí está implementada como tool.
- Si el usuario ya entregó datos suficientes en el mismo hilo, reutilízalos y evita volver a pedir exactamente los mismos campos.
";

    public static async Task<UserPromptSubmittedHookOutput?> OnUserPromptSubmittedAsync(UserPromptSubmittedHookInput input, HookInvocation invocation)
    {
        var prompt = input.Prompt.ToLowerInvariant();
        var offTopicKeywords = new[]
        {
            "bitcoin", "crypto", "ethereum", "nft",
            "receta", "cocinar", "cocina", "chef",
            "fútbol", "futbol", "baloncesto", "nba", "messi", "ronaldo",
            "clima", "pronóstico del tiempo", "pronostico",
            "política", "politica", "elecciones", "presidente",
            "película", "pelicula", "netflix", "serie",
            "chisme", "noticia", "diario", "periódico",
            "música", "musica", "canción", "cancion",
            "viaje personal", "vacaciones", "turismo",
            "programar en python", "javascript", "react",
            "dating", "cita", "amor", "pareja"
        };

        bool isOffTopic = offTopicKeywords.Any(k => prompt.Contains(k));

        // Bloqueo duro para temas claramente off-topic
        if (isOffTopic)
        {
            return new UserPromptSubmittedHookOutput
            {
                ModifiedPrompt = "[OFFTOPIC_BLOCK] El usuario ha preguntado algo fuera del dominio. Responde con el mensaje de rechazo estándar y no proceses más.",
                AdditionalContext = "BLOCKED_BY_DOMAIN_GUARD"
            };
        }

        return new UserPromptSubmittedHookOutput
        {
            ModifiedPrompt = input.Prompt
        };
    }
}
