# Base operativa SQLite/Turso

AgentOrion usa SQLite local como implementacion principal de persistencia. La capa se diseno para que el resto del sistema dependa de repositorios y de una fabrica de conexiones, no de detalles del archivo local. Eso deja preparado un adaptador futuro para Turso/libSQL sin reescribir tools, endpoints o flujos de agente.

## Schema y migraciones

- `TursoContext` crea la base si no existe y aplica migraciones versionadas en `SchemaMigrations`.
- La version actual del schema esta en `TursoContext.CurrentSchemaVersion`.
- Las migraciones son idempotentes: pueden correr sobre una DB nueva o sobre una DB existente.
- Las tablas principales son `Customers`, `Shipments`, `ShipmentEvents`, `AgentAuditLog`, `SimulatedEmails`, `ConversationMemory` y `SchemaMigrations`.

## Concurrencia local

Cada conexion aplica:

- `PRAGMA foreign_keys=ON`
- `PRAGMA busy_timeout=5000`
- `PRAGMA journal_mode=WAL`
- `PRAGMA synchronous=NORMAL`

Esto mejora integridad referencial y reduce choques de escritura locales mientras el agente guarda memoria, auditoria y eventos.

## Repositorios operativos

La base expone repositorios para:

- clientes y envios,
- memoria conversacional,
- auditoria de turnos de agente,
- timeline de eventos por envio/AWB,
- emails simulados.

Las operaciones criticas como crear AWB con su primer evento usan transacciones.

## Salud de base de datos

`GET /api/db/health` devuelve conectividad, proveedor, path activo, version de schema y PRAGMAs relevantes. No devuelve secretos ni credenciales.

## Turso futuro

La documentacion de Turso lista SDKs oficiales para varios lenguajes y .NET como ecosistema/comunidad. Por eso AgentOrion no se acopla aun a un paquete Turso especifico. La siguiente fase debe implementar un adaptador compatible con la fabrica de conexiones o un gateway SQL/HTTP manteniendo los repositorios como frontera estable.

## Copilot SDK

`GitHub.Copilot.SDK` queda fijado en `0.1.32` para evitar builds impredecibles por version flotante. La migracion a una linea GA debe validarse aparte con estos puntos:

- `CopilotClient`
- `SessionConfig`
- BYOK provider config
- streaming events
- hooks
- tools creadas con `AIFunctionFactory`
- sesiones persistentes y transitorias
