# AgentOrion - Diagrama de arquitectura actual

Este documento muestra la arquitectura actual de AgentOrion al nivel suficiente para entender como viaja una solicitud, donde viven los agentes, como se usan skills/tools/sesiones y donde estan las principales fortalezas y debilidades.

Archivo dedicado con solo diagramas: [DIAGRAMAS-ARQUITECTURA.md](DIAGRAMAS-ARQUITECTURA.md).
Guia del patron de workflows: [WORKFLOWS-HIBRIDOS.md](WORKFLOWS-HIBRIDOS.md).

## 1. Vista general

```mermaid
flowchart LR
    User["Usuario operador"] --> UI["Frontend React/Vite"]
    UI --> Api["ASP.NET Minimal API"]

    Api --> Chat["/api/chat SSE"]
    Api --> Crud["/api/customers /api/shipments"]
    Api --> Health["/health /api/db/health /api/runtime"]

    Chat --> Workflow["ChatTurnService"]

    Workflow --> Memory["ConversationMemoryService"]
    Workflow --> Coordinator["WorkflowCoordinator"]
    Coordinator --> AgentWorkflows["IAgentWorkflow[]"]
    AgentWorkflows --> AwbWorkflow["AwbReservationWorkflow"]
    AwbWorkflow --> Extractor["InputExtractor draft tipado"]
    AwbWorkflow --> Validator["Validator reglas de negocio"]
    AwbWorkflow --> Composer["ResponseComposer pregunta faltantes"]
    AwbWorkflow --> Deterministic["DeterministicTurnHandler"]
    Workflow --> Factory["AgentFactory"]
    Workflow --> Trace["AgentResponseTraceBuilder"]
    Workflow --> Audit["AgentAuditRepository"]

    Factory --> Router["AgentRequestRouter"]
    Router --> RoutingTrace["RoutingTrace score/confidence/reason"]
    Factory --> SDK["GitHub Copilot SDK"]
    Factory --> Skills["AgentOrion.Skills/*/SKILL.md"]
    Factory --> Tools["Tools AIFunction"]

    Tools --> Repos["Repositories"]
    Crud --> Repos
    Memory --> MemoryRepo["ConversationMemoryRepository"]
    Audit --> Db["SQLite agentorion.db"]
    Repos --> Db
    MemoryRepo --> Db

    SDK --> Provider["BYOK AI Provider"]
```

### Lectura rapida

- El frontend es una consola operativa con chat, AWBs recientes y clientes.
- El backend centraliza chat, CRUD, health, sesion de agente y tools.
- Los agentes no son procesos separados: son perfiles de sesion creados por `AgentFactory`.
- Los workflows de negocio viven antes del LLM: `WorkflowCoordinator` prueba flujos explicitos y cae al SDK si ninguno aplica.
- Las skills son carpetas markdown que se cargan en la sesion del Copilot SDK.
- SQLite es la memoria operativa local: clientes, envios, eventos, emails simulados, memoria y auditoria.

## 2. Capas del monorepo

```mermaid
flowchart TB
    subgraph Frontend["frontend/"]
        App["App.tsx"]
        ChatWindow["ChatWindow.tsx"]
        ApiClient["api/agentApi.ts"]
        Css["index.css"]
    end

    subgraph ApiLayer["backend/src/AgentOrion.Api"]
        Program["Program.cs"]
        ChatEndpoints["Endpoints/ChatEndpoints.cs SSE adapter"]
        CatalogJson["agent-catalog.json"]
    end

    subgraph Core["backend/src/AgentOrion.Core"]
        Models["Models"]
        Contracts["Persistence interfaces"]
        Options["Configuration"]
    end

    subgraph Infra["backend/src/AgentOrion.Infrastructure"]
        Copilot["Copilot orchestration + chat workflow"]
        Persistence["SQLite repositories"]
        Tools["AIFunction tools"]
        DbContext["TursoContext"]
    end

    subgraph Skills["backend/src/AgentOrion.Skills"]
        CoreDomain["core-domain"]
        AwbSkill["awb-dispatch"]
        ColdSkill["cold-chain"]
        CommSkill["client-comm"]
    end

    Frontend --> ApiLayer
    ApiLayer --> Core
    ApiLayer --> Infra
    Infra --> Core
    Infra --> Skills
```

### Estado actual

- La separacion por capas esta bien encaminada.
- Core contiene modelos y contratos, sin depender de SQLite ni del SDK.
- Infrastructure conoce SQLite, Copilot SDK y tools.
- Api registra dependencias, expone endpoints y sirve el build del frontend.
- `ChatEndpoints.cs` es transporte SSE; `ChatTurnService` vive en Infrastructure y coordina el turno completo.

## 3. Flujo completo de chat

```mermaid
sequenceDiagram
    participant U as Usuario
    participant FE as Frontend ChatWindow
    participant API as /api/chat SSE
    participant WF as ChatTurnService
    participant Mem as ConversationMemory
    participant Coord as WorkflowCoordinator
    participant Flow as IAgentWorkflow
    participant Det as Deterministic executor
    participant Fac as AgentFactory
    participant Rou as AgentRequestRouter
    participant SDK as Copilot SDK Session
    participant Tool as Tools
    participant DB as SQLite

    U->>FE: Escribe mensaje
    FE->>API: POST /api/chat {message, sessionId, mode}
    API->>WF: ProcessAsync(turno)
    WF->>Mem: Cargar/aplicar memoria si mode=memory
    WF->>Coord: TryHandleAsync(WorkflowContext)
    Coord->>Flow: Extraer + validar + ejecutar si aplica

    alt Workflow operativo aplica
        Flow->>Det: Ejecutar paso deterministico si hay datos suficientes
        Det->>DB: Buscar cliente / crear AWB / guardar evento
        WF->>DB: Guardar memoria y auditoria con trace
        API-->>FE: SSE delta/final/trace/done
    else Requiere LLM
        WF->>Fac: GetOrCreateSession
        Fac->>Rou: Seleccionar perfil especialista
        Rou-->>Fac: Route + Skills + Tools + Model + RoutingTrace
        Fac->>SDK: Crear/reusar sesion
        WF->>SDK: SendAndWaitAsync(prompt)
        SDK->>Tool: Ejecutar tool si aplica
        Tool->>DB: Repositorios SQLite
        SDK-->>WF: Eventos streaming
        WF->>DB: Guardar memoria y auditoria con trace
        API-->>FE: SSE delta/tool/trace/done
    end
```

### Puntos fuertes

- Hay dos caminos: deterministico y LLM. Eso baja latencia y costo en casos conocidos.
- El endpoint `/api/chat` ya es una capa delgada de transporte SSE.
- `ChatTurnService` centraliza el turno: lock de sesion, memoria, workflows, LLM, streaming, trace y auditoria.
- `WorkflowCoordinator` reduce acoplamiento: cada proceso implementa extraccion, validacion, ejecucion y respuesta propia.
- El frontend recibe trazabilidad: ruta, confianza, motivo, candidatos, modelo, tools, skills, tokens y duracion.
- La auditoria queda persistida en DB con explicacion de routing.

### Debilidades

- Ya hay contrato base de workflow, pero todavia no hay `WorkflowState` persistido para procesos largos.
- El extractor AWB actual usa reglas; un extractor IA con salida JSON tipada seria el siguiente salto para lenguaje mas libre.
- El trace es reusable, pero aun no existe un endpoint de consulta de auditoria para revisar historicos desde UI.

## 4. Agentes, rutas y skills

```mermaid
flowchart TB
    Prompt["Prompt del usuario"] --> Router["AgentRequestRouter"]

    Router --> General["operations-general"]
    Router --> Awb["awb-dispatch"]
    Router --> Cold["cold-chain"]
    Router --> Comm["client-comm"]
    Router --> Mixed["mixed-operations"]

    Awb --> AwbSkills["Skills: core-domain + awb-dispatch"]
    Cold --> ColdSkills["Skills: core-domain + cold-chain"]
    Comm --> CommSkills["Skills: core-domain + client-comm"]
    General --> CoreSkill["Skill: core-domain"]
    Mixed --> MixedSkills["Union de skills/tools relevantes"]

    Awb --> AwbTools["Tools: create_awb, get_awb_status, search_customer, register_customer"]
    Cold --> ColdTools["Tools: get_temperature_requirements, get_awb_status"]
    Comm --> CommTools["Tools: simulate_email, search_customer, register_customer, get_awb_status"]

    Router --> Score["Scoring de intencion"]
    Router --> Trace["RoutingTrace"]
    Score --> Strong["Senales fuertes: reserva, AWB, temperatura, correo"]
    Score --> Weak["Senales debiles: flores, pescado, frutas, mariscos"]
    Trace --> Confidence["confidence"]
    Trace --> Reason["reason"]
    Trace --> Candidates["candidate routes + matched signals"]
```

### Estado actual del router

- Ya no decide solo por `keyword.Contains`.
- Normaliza acentos, tokeniza el prompt y calcula score por ruta.
- Senales fuertes pesan mas que productos ambiguos.
- Devuelve `RoutingTrace` con candidatos, score, senales, confianza y explicacion.
- La confianza y la razon viajan al frontend y se guardan en auditoria.
- Hay dataset inicial de evaluacion en `backend/tests/AgentOrion.Api.Tests/routing-evaluation-cases.json`.
- Ejemplo: `crear reserva AWB para flores` va a AWB, no a mixto.
- Ejemplo: `validar temperatura para flores` va a cadena de frio.
- Ejemplo: `notificar alerta de temperatura del AWB` va a mixto.

### Debilidades restantes

- El scoring sigue siendo manual.
- El dataset de evaluacion todavia es pequeno.
- No hay calibracion estadistica ni pesos aprendidos desde ejemplos historicos.
- No hay evaluacion semantica de respuesta final, solo de ruta/intencion.

### Mejora recomendada

Evolucionar el router hacia `AgentEvaluation`:

- dataset versionado de prompts reales,
- ruta esperada,
- tools esperadas,
- umbral de confianza,
- respuesta esperada o criterios de calidad,
- reporte automatico de regresiones.

## 5. Sesiones y memoria

```mermaid
flowchart LR
    SessionId["sessionId frontend"] --> Mode{"mode"}
    Mode --> Fast["fast"]
    Mode --> Memory["memory"]

    Fast --> Transient["Sesion SDK transitoria"]
    Transient --> Delete["Se borra al terminar turno"]

    Memory --> Active["Sesion SDK activa en memoria"]
    Active --> TTL["TTL 20 minutos"]
    Active --> SameRoute{"Misma ruta?"}
    SameRoute -->|Si| Reuse["Reusar sesion"]
    SameRoute -->|No| Recreate["Eliminar y recrear sesion especialista"]

    Memory --> Structured["ConversationMemory persistida"]
    Structured --> Customer["Customer memory"]
    Structured --> Shipment["Shipment memory"]
    Structured --> LastRoute["LastRouteName"]
    Structured --> Db["SQLite ConversationMemory"]
```

### Lo que esta bien

- `fast` evita memoria innecesaria y reduce contexto.
- `memory` permite continuidad por sesion.
- La memoria durable esta en SQLite, no solo en el SDK.
- Si cambia el especialista, la sesion se recrea para evitar mezclar instrucciones incompatibles.

### Riesgos

- Las sesiones SDK activas viven solo en memoria del proceso.
- Si se reinicia el backend, se pierde la sesion viva aunque la memoria estructurada siga.
- La memoria se extrae con reglas regex/manuales; puede fallar en lenguaje mas libre.
- No hay memoria por cliente a largo plazo, solo por conversacion.

### Mejora recomendada

Separar memoria en cuatro niveles:

```mermaid
flowchart TB
    M1["Memoria de turno: prompt actual + trace"]
    M2["Memoria de sesion: ConversationMemory"]
    M3["Memoria de entidad: cliente/envio historico"]
    M4["Memoria aprendida: reglas aprobadas"]

    M1 --> M2
    M2 --> M3
    M3 --> M4
```

## 6. Persistencia SQLite/Turso

```mermaid
erDiagram
    Customers ||--o{ Shipments : owns
    Shipments ||--o{ ShipmentEvents : has
    Shipments ||--o{ SimulatedEmails : has
    ConversationMemory ||--|| Session : stores
    AgentAuditLog ||--|| Session : traces
    SchemaMigrations ||--|| Database : versions

    Customers {
        int Id PK
        string FullName
        string Email
        string Phone
        string CompanyName
        string Country
        string Address
        string DocumentNumber
        string CreatedAt
    }

    Shipments {
        int Id PK
        string AwbNumber
        int CustomerId FK
        string ProductType
        string ProductName
        real QuantityKg
        real TemperatureRequiredC
        string OriginAirport
        string DestinationAirport
        string FlightDate
        string Status
        string PhytosanitaryCert
        string CreatedAt
    }

    ShipmentEvents {
        int Id PK
        int ShipmentId FK
        string EventType
        string EventData
        string RecordedAt
    }

    ConversationMemory {
        string SessionId PK
        string LastRouteName
        string CurrentIntent
        string CustomerJson
        string ShipmentJson
        string UpdatedAt
    }

    AgentAuditLog {
        int Id PK
        string SessionId
        string UserPrompt
        string AgentResponse
        string RouteName
        string RouteDisplayName
        string Model
        string ChatMode
        string ToolsJson
        string SkillsJson
        string UsageJson
        string RoutingTraceJson
        real RouteConfidence
        string RoutingReason
        real DurationMs
        int WasOffTopic
        string CreatedAt
    }
```

### Fortalezas actuales

- Hay migraciones versionadas (`SchemaMigrations`).
- Cada conexion activa foreign keys, busy timeout y WAL.
- Hay indices para AWB, cliente, eventos, memoria y auditoria.
- Crear AWB + evento inicial es transaccional.
- Hay endpoint `/api/db/health`.
- Existe abstraccion `IAgentOrionDbConnectionFactory`, util para un adaptador Turso futuro.

### Debilidades actuales

- Las migraciones aun viven dentro de `TursoContext`; cuando crezcan, convendra moverlas a clases/archivos separados.
- No hay unit of work general para flujos complejos multi-repositorio.
- `ConversationMemory` guarda JSON, util ahora, pero dificil de consultar analiticamente.
- No hay versionado de memoria aprendida ni reglas aprobadas.
- No hay soft delete, revision historica ni control de cambios por usuario/sistema.

## 7. Autoaprendizaje: estado real

```mermaid
flowchart LR
    Audit["AgentAuditLog"] --> Analysis["Analisis de patrones"]
    Memory["ConversationMemory"] --> Analysis
    Events["ShipmentEvents"] --> Analysis

    Analysis --> Suggestions["LearningSuggestion"]
    Suggestions --> Review["Revision humana"]
    Review --> Approved["ApprovedKnowledge"]
    Approved --> Skills["Skills / Router / Workflows"]
```

### Estado actual

El sistema todavia no autoaprende de verdad. Hoy tiene:

- memoria estructurada de conversacion,
- auditoria de turnos,
- skills estaticas,
- tools operativas,
- eventos de negocio.

Eso es una base correcta para aprender despues, pero no es aprendizaje autonomo.

### Forma segura de construir aprendizaje

El aprendizaje deberia ser supervisado:

1. Detectar patrones desde auditoria y eventos.
2. Generar sugerencias.
3. Aprobar o rechazar sugerencias.
4. Versionar conocimiento aprobado.
5. Inyectar conocimiento aprobado en skills/router/workflows.
6. Medir si mejora con evaluaciones.

Nunca conviene que el agente edite sus skills sin aprobacion.

## 8. Fortalezas principales

| Area | Fortaleza |
|------|-----------|
| Arquitectura | Buena separacion Api/Core/Infrastructure/Skills |
| Agentes | Especialistas configurados por catalogo |
| Tools | Acciones reales encapsuladas con `AIFunctionFactory` |
| DB | SQLite endurecido con migraciones, PRAGMAs e indices |
| Memoria | Estado estructurado por sesion |
| Auditoria | Turnos, trazas, confianza y explicacion de routing persistidas |
| Router | Scoring por senales, confianza y dataset inicial de evaluacion |
| Workflow | `WorkflowCoordinator` + extractor/validator/composer por proceso |
| Testing | Hay pruebas backend/frontend y dataset de routing |
| Evolucion | Abstraccion de conexion prepara Turso/libSQL |

## 9. Debilidades principales

| Area | Debilidad | Riesgo |
|------|-----------|--------|
| Router | Scoring manual y dataset pequeno | Puede fallar en prompts nuevos |
| Skills | Son mas documentacion que playbooks | El agente puede actuar de forma inconsistente |
| Workflows | No hay `WorkflowState` persistido ni extractor IA estructurado | Flujos largos aun dependen de memoria heuristica |
| Memoria | Solo sesion, no entidad/aprendizaje | No aprende entre clientes o operaciones |
| DB | Migraciones en `TursoContext` | Se volvera pesado con mas schema |
| Evaluacion | Solo cubre routing inicial | No mide herramientas ni calidad de respuesta |
| Seguridad | Confirmaciones limitadas | Riesgo al conectar endpoints reales |

## 10. Nodos de mejora recomendados

```mermaid
flowchart TB
    A["Estado actual"] --> B["Router con RoutingTrace implementado"]
    B --> C["Workflows hibridos base"]
    C --> D["WorkflowState persistido"]
    D --> E["Memoria por entidad"]
    E --> F["Aprendizaje supervisado"]
    F --> G["Evaluaciones automaticas"]
    G --> H["Turso remoto / integraciones reales"]

    C --> C1["AWB reservation workflow"]
    C --> C2["Customer onboarding workflow"]
    C --> C3["Cold-chain alert workflow"]
    C --> C4["Client notification workflow"]

    E --> E1["LearningSuggestion"]
    E --> E2["ApprovedKnowledge"]
    E --> E3["Skill versioning"]
```

### Prioridad sugerida

1. Extractores IA por workflow: salida JSON tipada, confianza, pruebas golden y fallback a reglas.
2. `WorkflowState`: formalizar pasos de reserva, cliente, alerta y notificacion cuando el flujo sea multi-turno.
3. `AgentEvaluation`: ampliar dataset a ruta + campos extraidos + tool + respuesta + criterios de calidad.
4. `ApprovedKnowledge`: aprendizaje supervisado y versionado.
5. Endpoint/UI de auditoria para revisar trazas historicas.
6. Adaptador Turso remoto cuando la base local ya este estable.

## 11. Decision tecnica actual

AgentOrion ya no es solo un chatbot. La arquitectura actual se parece mas a un copiloto operativo con:

- agentes especialistas,
- tools con acciones de negocio,
- memoria estructurada,
- auditoria persistida con explicacion de routing,
- base SQLite preparada para crecer,
- routing mejorado por scoring y evaluacion inicial.

El siguiente salto no deberia ser "mas prompts". El siguiente salto deberia ser convertir intenciones en workflows hibridos, versionados y medibles: IA para entender/redactar; codigo para validar/ejecutar.
