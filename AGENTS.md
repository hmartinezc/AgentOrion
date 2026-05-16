# AgentOrion — Guía para Agentes de Código

## Convenciones del proyecto

- **Lenguaje**: C# 13 (.NET 9), React 19 + TypeScript (frontend).
- **Arquitectura**: Monorepo. Backend en `backend/`, frontend en `frontend/`.
- **Backend**: Clean Architecture con 3 proyectos (Core, Infrastructure, Api).
- **Frontend**: React con Vite. Build se sirve como estáticos desde ASP.NET.
- **Base de datos**: SQLite local via `Microsoft.Data.Sqlite` (migrable a Turso). Archivo en `data/agentorion.db`.

## Patrones clave

- **Repository Pattern**: Interfaces en `AgentOrion.Core/Persistence/`, implementaciones en `AgentOrion.Infrastructure/Persistence/Repositories/`.
- **Copilot SDK**: `AgentFactory` (Singleton) crea `CopilotClient` con BYOK. `SessionConfig` con `SkillDirectories`, `SystemMessage`, `Hooks` y `Tools`.
- **Tools**: Definidas con `AIFunctionFactory.Create(MethodInfo, object, name, description)` para evitar problemas de inferencia de lambdas complejas. Métodos de instancia en clases `*ToolService`.
- **Domain Guard**: 3 capas (SystemMessage + Skill `core-domain` + Hook `OnUserPromptSubmitted`).
- **Skills**: Carpetas en `AgentOrion.Skills/*/SKILL.md`. Se cargan automáticamente por ruta en `AgentOrionOptions.SkillDirectories`.

## Endpoints importantes

- `GET /health` — Health check.
- `POST /api/chat` — Chat con streaming SSE (`text/event-stream`). Recibe `{ message, sessionId? }`.
- `GET/POST /api/customers` — CRUD clientes.
- `GET /api/shipments` y `GET /api/shipments/{awb}` — CRUD envíos.

## Variables de entorno críticas

- `AgentOrion__Copilot__Provider__ApiKey` — API key del proveedor de IA (OpenAI, Azure, etc.).
- `AgentOrion__Copilot__Provider__BaseUrl` — Endpoint del modelo.
- `AgentOrion__Copilot__Model` — Nombre del modelo.

## Build y run

```bash
# Backend
cd backend && dotnet run --project src/AgentOrion.Api

# Frontend dev
cd frontend && npm install && npm run dev

# Producción (Docker)
docker build -t agentorion .
docker run -p 8080:8080 \
  -e AgentOrion__Copilot__Provider__ApiKey=sk-XXX \
  -e AgentOrion__DbPath=data/agentorion.db \
  -v agentorion-data:/app/data \
  agentorion
```

## Errores comunes y soluciones

| Error | Causa | Solución |
|-------|-------|----------|
| `CS8917` al crear AIFunction | Lambda compleja con records | Usar `AIFunctionFactory.Create(MethodInfo, object, name, desc)` con método en clase separada |
| `NU1605` Microsoft.Extensions.AI.Abstractions | Dependencia duplicada | NO agregar referencia directa; GitHub.Copilot.SDK la provee |
| SSE .Wait() deadlock | async-over-sync en callback síncrono | Usar `Response.Body.Write(bytes)` síncrono en vez de `WriteAsync().Wait()` |
| DbPath incorrecto en Docker | path relativo desde appsettings.dev | Sobreescribir con `AgentOrion__DbPath=data/agentorion.db` en env vars |

## Agregar una nueva Tool

1. Crea un método en una clase `*ToolService` en `AgentOrion.Infrastructure/Tools/`.
2. Crea un método factory estático en la clase `*Tools` que llame `AIFunctionFactory.Create(method, serviceInstance, name, description)`.
3. Regístrala en `ChatEndpoints.cs` en el array `tools`.
4. Documenta parámetros con `[Description("...")]`.

## Agregar una nueva Skill

1. Crea carpeta `AgentOrion.Skills/nombre-skill/`.
2. Crea `SKILL.md` con contenido markdown (opcional YAML frontmatter con `name` y `description`).
3. Reinicia el backend.

## No modificar sin consultar

- `TursoContext.cs` — El schema SQL debe mantenerse sincronizado con los modelos.
- `AgentFactory.cs` — Aquí se configura BYOK y Custom Agents. Cualquier cambio afecta la autenticación.
