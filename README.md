# AgentOrion — Motor de IA para Agencias de Carga Perecedera

Monorepo con backend ASP.NET Core 9 + GitHub Copilot SDK (BYOK) + Turso (SQLite local) y frontend React + Vite.

## Arquitectura Simplificada

```
React (Vite)  <--SSE-->  ASP.NET Core 9  <--BYOK-->  OpenAI/Azure/Ollama
                              |
                         Turso (SQLite)
```

El agente está protegido con **Domain Guard** de 3 capas: solo responde temas de cargas perecederas (AWB, flores, pescado, frutas, cadena de frío).

## Requisitos

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js 20+](https://nodejs.org/)
- API Key de tu proveedor de IA (OpenAI, Azure, Anthropic, etc.) o [Ollama](https://ollama.com/) local.

## Correr en local

### Opcion rapida

```powershell
pwsh ./scripts/start-local.ps1
```

Esto abre dos ventanas separadas:

- backend en `http://localhost:5000`
- frontend en `http://localhost:5173`

Para detener ambos:

```powershell
pwsh ./scripts/stop-local.ps1
```

### Opcion manual (paso a paso)

### 1. Backend

```bash
dotnet run --project backend/src/AgentOrion.Api/AgentOrion.Api.csproj --launch-profile AgentOrion.Api
```

La API queda en `http://localhost:5000`.

### 2. Frontend

```bash
cd frontend
npm install
npm run dev
```

El dashboard queda en `http://localhost:5173`.

### 3. Configurar tu API Key y proveedor

Edita `backend/src/AgentOrion.Api/appsettings.Development.json`:

```json
{
  "AgentOrion": {
    "Copilot": {
      "Provider": {
        "ApiKey": "sk-XXXXXXXXXXXX"
      }
    }
  }
}
```

> También puedes usar la variable de entorno `AgentOrion__Copilot__Provider__ApiKey`.

### 4. Probar

Abre el dashboard, saluda al agente y pídele:
- *"Crea un AWB para 500kg de rosas de BOG a AMS"*
- *"Consulta estado del AWB-FLO-XXXXXX"*
- *"Registra cliente Juan Pérez, email juan@exporta.com"*
- *"Simula email de confirmación al cliente para el AWB creado"*

## Docker (local o servidor)

```bash
export APP_KEY=sk-XXX    # o pon directo
docker build -t agentorion .
docker run -p 8080:8080 \
  -e AgentOrion__Copilot__Provider__ApiKey=$APP_KEY \
  -e AgentOrion__Copilot__Model=gpt-4.1 \
  -e AgentOrion__DbPath=data/agentorion.db \
  -v agentorion-data:/app/data \
  agentorion
```

> Copia `.env.example` como `.env` para tener las variables listas con `docker compose up -d`.

Abre `http://localhost:8080`.

## Desplegar en Coolify (Hetzner)

Ve a `docs/DOCKER.md` para la guía completa paso a paso.

## Otros docs utiles

- `docs/CONFIGURACION.md` - proveedores BYOK, incluyendo OpenCode Go.
- `docs/MAF.md` - cuando usar Microsoft Agent Framework y un caso concreto para AgentOrion.
- `docs/MEMORIA-ESTRUCTURADA.md` - modos `fast`/`memory` y memoria persistida en SQLite.

## Estructura de Skills

Las skills están en `backend/src/AgentOrion.Skills/`:
- `core-domain/` — Identidad y rechazo off-topic.
- `awb-dispatch/` — Conocimiento de guías aéreas y exportación.
- `cold-chain/` — Temperaturas y cadena de frío.
- `client-comm/` — Comunicación con exportadores.

Para agregar una nueva skill, crea una carpeta con `SKILL.md` y reinicia el backend.

## Licencia

MIT
