# Despliegue con Docker y Coolify

Este documento explica cómo desplegar AgentOrion en un VPS Hetzner (o cualquier servidor Linux) usando Docker y Coolify.

## Docker local (prueba rápida)

Desde la raíz del monorepo:

```bash
docker build -t agentorion:latest .
docker run -d \
  -p 8080:8080 \
  -e ASPNETCORE_URLS="http://+:8080" \
  -e AgentOrion__Copilot__Provider__ApiKey="sk-XXXXXXXX" \
  -e AgentOrion__DbPath=data/agentorion.db \
  -v agentorion-data:/app/data \
  --name agentorion \
  agentorion:latest
```

Visita `http://localhost:8080`.

## Despliegue en Coolify (Hetzner)

### 1. Prepara tu servidor

- Compra un VPS en Hetzner (Ubuntu 22.04/24.04, mínimo 2 vCPU / 4GB RAM).
- Conéctate por SSH.
- Instala Docker y Coolify siguiendo la [guía oficial de Coolify](https://coolify.io/docs/installation).

### 2. Crea el recurso en Coolify

1. En el dashboard de Coolify, crea un nuevo **Project**.
2. Dentro del proyecto, crea un nuevo **Resource** → **Application**.
3. Selecciona **Dockerfile** como fuente.
4. Conecta tu repo de Git (GitHub/GitLab) o sube el código manualmente.

### 3. Configura el build

- **Build directory**: `.` (raíz del repo)
- **Dockerfile path**: `Dockerfile`
- **Port**: `8080`
- **Health check**: `/health`

### 4. Variables de entorno (importante)

En Coolify, ve a la pestaña **Environment Variables** y agrega:

| Variable | Valor de ejemplo | Descripción |
|----------|-----------------|-------------|
| `ASPNETCORE_URLS` | `http://+:8080` | Puerto de escucha |
| `AgentOrion__Copilot__Provider__ApiKey` | `sk-XXXXXXXX` | Tu API key de IA |
| `AgentOrion__Copilot__Model` | `gpt-4.1` | Modelo a usar |
| `AgentOrion__Copilot__Provider__Type` | `openai` | Tipo de proveedor |
| `AgentOrion__Copilot__Provider__BaseUrl` | `https://api.openai.com/v1` | Endpoint del modelo |
| `AgentOrion__DbPath` | `data/agentorion.db` | Ruta BD SQLite (relativo a /app) |

> Para Ollama local, necesitarás correr Ollama en el mismo servidor o en otro contenedor y apuntar `BaseUrl` a `http://ollama:11434/v1`.

### 5. Volumen persistente

En Coolify, agrega un volumen para que la base de datos SQLite (Turso) persista entre reinicios:

- **Volume name**: `agentorion-data`
- **Container path**: `/app/data`

### 6. Deploy

Haz clic en **Deploy**.

Coolify hará:
1. `git clone` (o pull).
2. `docker build` usando el `Dockerfile`.
3. `docker run` con las variables y volumen configurados.
4. Health check en `/health`.

### 7. Dominio

En la pestaña **Domains** de Coolify, asigna tu dominio (ej: `agentorion.tudominio.com`). Coolify configura automáticamente Traefik/Nginx con SSL (Let's Encrypt).

### Actualizaciones futuras

Cuando hagas `git push` a tu repo, Coolify puede auto-desplegar si activas el webhook.

## Docker Compose (alternativa sin Coolify)

Si prefieres Docker Compose puro en tu servidor:

```yaml
# docker-compose.yml
version: '3.8'
services:
  agentorion:
    build: .
    ports:
      - "8080:8080"
    environment:
      - ASPNETCORE_URLS=http://+:8080
      - AgentOrion__Copilot__Provider__ApiKey=${OPENAI_API_KEY}
      - AgentOrion__DbPath=data/agentorion.db
    volumes:
      - agentorion-data:/app/data
    restart: unless-stopped

volumes:
  agentorion-data:
```

Correr:
```bash
export OPENAI_API_KEY=sk-XXX
docker-compose up -d
```

## Notas de seguridad

- Nunca commitees tu API key. Usa variables de entorno.
- El contenedor expone solo el puerto 8080.
- El frontend se sirve como archivos estáticos desde el backend, no hay servidor aparte.
- La base de datos SQLite está en el volumen `/app/data`; respálala periódicamente.
