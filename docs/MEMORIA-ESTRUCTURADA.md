# Memoria Estructurada y Modos de Chat

## Objetivo

AgentOrion usa dos capas de memoria:

1. **Memoria conversacional del SDK**
2. **Memoria estructurada propia en SQLite**

La primera ayuda al LLM a continuar el hilo. La segunda guarda datos de negocio ya capturados para no volver a pedirlos cuando cambia el especialista.

## Modos

### Modo rapido

- Crea una sesion efimera por turno.
- Aplica routing dinamico por especialista.
- Carga solo las skills y tools del especialista detectado.
- Menor latencia y menor consumo.
- No mantiene contexto entre mensajes.

### Modo con memoria

- Mantiene una sesion viva por conversacion.
- Usa routing dinamico por especialista.
- Si el siguiente turno sigue en la misma area, reutiliza la sesion actual.
- Si el siguiente turno cambia de area, recrea la sesion con el nuevo especialista.
- Antes de recrear la sesion, inyecta una **memoria estructurada** al system prompt para transferir el contexto de negocio ya capturado.

## Que guarda la memoria estructurada

Tabla SQLite: `ConversationMemory`

Campos:

- `SessionId`
- `LastRouteName`
- `CurrentIntent`
- `CustomerJson`
- `ShipmentJson`
- `UpdatedAt`

### CustomerJson

- `CustomerId`
- `FullName`
- `Email`
- `Phone`
- `CompanyName`
- `Country`
- `Address`
- `DocumentNumber`

### ShipmentJson

- `AwbNumber`
- `ProductType`
- `ProductName`
- `QuantityKg`
- `OriginAirport`
- `DestinationAirport`
- `FlightDate`
- `TemperatureRequiredC`
- `Status`

## Como se llena

La memoria estructurada se actualiza por dos vias:

1. **Heuristicas desde el prompt del usuario**
2. **Resultados de tools**

Ejemplos:

- Si el usuario dice `Se llama JAF Flower y su email es jaf@gmail.com`, esos campos se guardan.
- Si `register_customer` devuelve `customerId`, ese ID queda guardado.
- Si `create_awb` devuelve `awbNumber`, ese AWB queda guardado.

## Beneficio arquitectonico

Esto permite que el flujo sea dinamico por especialista sin perder continuidad operativa.

Ejemplo:

1. Especialista de clientes recoge nombre, email y direccion.
2. El usuario luego pide crear una reserva AWB.
3. El sistema cambia al especialista de AWB.
4. La memoria estructurada le pasa al nuevo especialista los datos ya capturados.
5. El agente no vuelve a pedir nombre, email o cliente si ya los tiene.

## Archivos principales

- `backend/src/AgentOrion.Infrastructure/Copilot/ConversationMemoryService.cs`
- `backend/src/AgentOrion.Infrastructure/Copilot/AgentFactory.cs`
- `backend/src/AgentOrion.Infrastructure/Copilot/AgentRequestRouter.cs`
- `backend/src/AgentOrion.Api/Endpoints/ChatEndpoints.cs`
- `backend/src/AgentOrion.Infrastructure/Persistence/Repositories/ConversationMemoryRepository.cs`
- `backend/src/AgentOrion.Infrastructure/Persistence/TursoContext.cs`

## Limitacion actual

La memoria estructurada es heuristica, no semantica. Es decir, hoy reconoce patrones comunes y resultados de tools, pero no hace un resumen inteligente libre de todo el hilo.

Eso es intencional: se priorizo simplicidad, trazabilidad y bajo costo.
