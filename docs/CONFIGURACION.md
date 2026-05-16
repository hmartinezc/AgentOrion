# Configuración de AgentOrion

## Opciones disponibles (`appsettings.json`)

```json
{
  "AgentOrion": {
    "DbPath": "../../../data/agentorion.db",
    "SkillDirectories": [
      "../AgentOrion.Skills"
    ],
    "Copilot": {
      "Model": "gpt-4.1",
      "Provider": {
        "Type": "openai",
        "BaseUrl": "https://api.openai.com/v1",
        "ApiKey": "",
        "WireApi": "completions"
      }
    }
  }
}
```

## Proveedores soportados (BYOK)

### OpenCode Go (OpenAI-compatible)
Para los modelos de `chat/completions` de OpenCode Go, usa `type: "openai"` y como `BaseUrl` la raiz `v1`.

```json
"Copilot": {
  "Model": "qwen3.5-plus",
  "Provider": {
    "Type": "openai",
    "BaseUrl": "https://opencode.ai/zen/go/v1",
    "ApiKey": "tu-api-key",
    "WireApi": "completions"
  }
}
```

Modelos recomendados para primera prueba:

| Modelo | Uso recomendado |
|--------|-----------------|
| `qwen3.5-plus` | Recomendado para primera prueba real con AgentOrion |
| `deepseek-v4-flash` | Rapido y economico, pero puede fallar en flujos con reasoning/tool chaining |
| `qwen3.6-plus` | Mejor razonamiento |
| `glm-5.1` | Razonamiento mas fuerte, mayor costo |
| `kimi-k2.6` | Alternativa fuerte para tareas largas |

Notas importantes:

- Para Copilot SDK usa el **ID real del modelo**, por ejemplo `deepseek-v4-flash`, no el formato `opencode-go/<model-id>` de OpenCode.
- Para esta v1 conviene empezar con modelos que usen `chat/completions`.
- En nuestras pruebas, `qwen3.5-plus` fue el modelo mas estable para flujo completo con tools, skills y custom agents.
- Los modelos `MiniMax M2.5` y `MiniMax M2.7` usan endpoint tipo `messages`; no los recomiendo para la primera prueba de esta app.

### OpenAI (por defecto)
```json
"Provider": {
  "Type": "openai",
  "BaseUrl": "https://api.openai.com/v1",
  "ApiKey": "sk-XXXXXXXX",
  "WireApi": "completions"
}
```

### Ollama (local, gratis)
```json
"Provider": {
  "Type": "openai",
  "BaseUrl": "http://localhost:11434/v1",
  "ApiKey": "",
  "WireApi": "completions"
}
```
> Asegúrate de tener Ollama corriendo con un modelo como `llama3` o `phi4`.

### Azure OpenAI
```json
"Provider": {
  "Type": "azure",
  "BaseUrl": "https://mi-recurso.openai.azure.com",
  "ApiKey": "clave-azure",
  "WireApi": "completions"
}
```

### Anthropic (Claude)
```json
"Provider": {
  "Type": "anthropic",
  "BaseUrl": "https://api.anthropic.com",
  "ApiKey": "sk-ant-XXXXXXXX",
  "WireApi": "completions"
}
```

## Variables de entorno (útiles para Docker)

Puedes sobreescribir cualquier valor de `appsettings.json` usando variables de entorno con doble guión bajo:

```bash
AgentOrion__Copilot__Provider__ApiKey=sk-XXX
AgentOrion__Copilot__Provider__BaseUrl=https://opencode.ai/zen/go/v1
AgentOrion__Copilot__Model=deepseek-v4-flash
AgentOrion__DbPath=/app/data/agentorion.db
```

## Modelos recomendados

| Proveedor | Modelo | Notas |
|-----------|--------|-------|
| OpenAI | `gpt-4.1` | Rápido, bueno para tools. |
| OpenAI | `gpt-5` | Si tienes acceso. Usa `wireApi: "responses"`. |
| Ollama | `llama3` | Gratis, local. Más lento. |
| Ollama | `phi4` | Microsoft, buen balance. |

## Skills personalizadas

1. Crea una carpeta en `backend/src/AgentOrion.Skills/nueva-skill/`.
2. Agrega un archivo `SKILL.md` con contenido markdown.
3. Reinicia el backend.
4. Opcional: agrega la skill a un agente custom en `AgentFactory.cs`.

## Troubleshooting

### "Model not specified"
Asegúrate de que `Model` no esté vacío cuando usas BYOK.

### "Authentication failed"
Verifica que la `ApiKey` sea válida y no esté expirada.

### El agente responde off-topic
Revisa que `SkillDirectories` apunte a la carpeta correcta y que `core-domain/SKILL.md` exista.
