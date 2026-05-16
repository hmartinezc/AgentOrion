# Multi-stage Dockerfile: compila frontend + backend y sirve todo desde ASP.NET

# ---------- Stage 1: Build Frontend ----------
FROM node:20-alpine AS frontend-build
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json* ./
RUN npm install
COPY frontend/ .
RUN npm run build

# ---------- Stage 2: Build Backend ----------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS backend-build
WORKDIR /src/backend
COPY backend/ .
RUN dotnet publish src/AgentOrion.Api/AgentOrion.Api.csproj -c Release -o /app/publish

# ---------- Stage 3: Final Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Copiar publicación del backend
COPY --from=backend-build /app/publish .

# Copiar build del frontend a wwwroot (ASP.NET servirá estáticos desde aquí)
COPY --from=frontend-build /src/frontend/dist ./wwwroot

# Crear directorio de datos para Turso SQLite
RUN mkdir -p /app/data

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget --no-verbose --tries=1 --spider http://localhost:8080/health || exit 1

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AgentOrion.Api.dll"]
