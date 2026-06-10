# Diagramas de arquitectura AgentOrion

Este archivo concentra solo los diagramas para revisar la arquitectura sin tener que navegar todo el documento largo. Si el visor no renderiza Mermaid, abre este archivo en VS Code con extension Mermaid, GitHub, Obsidian, o un visor compatible.

## 1. Vista general actual

```mermaid
flowchart LR
    User["Usuario operador"] --> UI["Frontend React/Vite"]
    UI --> Api["ASP.NET Minimal API"]

    Api --> Chat["/api/chat SSE"]
    Api --> Crud["CRUD clientes/envios"]
    Api --> Health["Health/runtime/db health"]

    Chat --> Workflow["ChatTurnService"]
    Workflow --> Memory["ConversationMemoryService"]
    Workflow --> Coordinator["WorkflowCoordinator"]
    Coordinator --> AgentWorkflows["IAgentWorkflow[]"]
    AgentWorkflows --> AwbWorkflow["AwbReservationWorkflow"]
    AwbWorkflow --> Extractor["InputExtractor"]
    AwbWorkflow --> Validator["Validator"]
    AwbWorkflow --> Composer["ResponseComposer"]
    AwbWorkflow --> Deterministic["Deterministic executor"]
    Workflow --> Factory["AgentFactory"]
    Workflow --> Trace["AgentResponseTraceBuilder"]
    Workflow --> Audit["AgentAuditRepository"]

    Factory --> Router["AgentRequestRouter"]
    Router --> RoutingTrace["RoutingTrace confidence/reason/candidates"]
    Factory --> SDK["GitHub Copilot SDK"]
    Factory --> Skills["AgentOrion.Skills/*/SKILL.md"]
    Factory --> Tools["AIFunction tools"]

    Tools --> Repos["Repositories"]
    Crud --> Repos
    Memory --> MemoryRepo["ConversationMemoryRepository"]
    Audit --> Db["SQLite data/agentorion.db"]
    Repos --> Db
    MemoryRepo --> Db

    SDK --> Provider["BYOK AI Provider"]
```

## 2. Capas del monorepo

```mermaid
flowchart TB
    subgraph Frontend["frontend/"]
        App["App.tsx"]
        ChatWindow["ChatWindow.tsx"]
        AgentApi["api/agentApi.ts"]
        Styles["index.css"]
    end

    subgraph ApiLayer["backend/src/AgentOrion.Api"]
        Program["Program.cs"]
        ChatEndpoints["ChatEndpoints.cs SSE adapter"]
        Catalog["agent-catalog.json"]
    end

    subgraph Core["backend/src/AgentOrion.Core"]
        Models["Domain models"]
        Contracts["Persistence interfaces"]
        Config["AgentOrionOptions"]
    end

    subgraph Infrastructure["backend/src/AgentOrion.Infrastructure"]
        Workflow["ChatTurnService"]
        Copilot["AgentFactory + Router + Trace"]
        Persistence["SQLite repositories"]
        Tools["Tool services + AIFunctionFactory"]
        DbContext["TursoContext migrations/pragmas"]
    end

    subgraph Skills["backend/src/AgentOrion.Skills"]
        CoreDomain["core-domain"]
        AwbSkill["awb-dispatch"]
        ColdSkill["cold-chain"]
        CommSkill["client-comm"]
    end

    Frontend --> ApiLayer
    ApiLayer --> Core
    ApiLayer --> Infrastructure
    Infrastructure --> Core
    Infrastructure --> Skills
```

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
    FE->>API: POST /api/chat
    API->>WF: ProcessAsync(message, sessionId, mode)
    WF->>Mem: Cargar/aplicar memoria si mode=memory
    WF->>Coord: TryHandleAsync(WorkflowContext)
    Coord->>Flow: Extraer + validar + ejecutar si aplica

    alt Workflow operativo aplica
        Flow->>Det: Ejecutar paso deterministico
        Det->>DB: Buscar cliente / crear AWB / guardar evento
        WF->>DB: Persistir memoria + audit log + routing trace
        WF-->>API: ChatStreamEvent delta/final/trace/done
        API-->>FE: SSE delta/final/trace/done
    else Requiere LLM
        WF->>Fac: GetOrCreateSessionAsync
        Fac->>Rou: SelectProfileWithTrace
        Rou-->>Fac: Perfil + RoutingTrace
        Fac->>SDK: Crear/reusar sesion con skills/tools/BYOK
        WF->>SDK: SendAndWaitAsync
        SDK->>Tool: Ejecutar tool si aplica
        Tool->>DB: Repositorios SQLite
        SDK-->>WF: Eventos streaming/tools/usage
        WF->>DB: Persistir memoria + audit log + routing trace
        WF-->>API: ChatStreamEvent stream
        API-->>FE: SSE stream
    end
```

## 4. Workflow hibrido por proceso

```mermaid
flowchart LR
    Context["WorkflowContext"] --> Coordinator["WorkflowCoordinator"]
    Coordinator --> Workflows["IAgentWorkflow[]"]
    Workflows --> Awb["AwbReservationWorkflow"]

    Awb --> Extractor["Extractor rules hoy / IA JSON futuro"]
    Extractor --> Draft["AwbReservationDraft"]
    Draft --> Validator["Validator C#"]
    Validator -->|No aplica| Fallback["Fallback a Copilot SDK"]
    Validator -->|Faltan datos| Composer["ResponseComposer"]
    Composer --> Ask["Pregunta concreta al usuario"]
    Validator -->|Listo| Executor["DeterministicTurnHandler / Tools"]
    Executor --> Gateway["IAwbReservationGateway"]
    Gateway --> Db["SQLite hoy"]
    Gateway --> Http["HTTP APIs futuro"]
```

## 5. Router de agentes, skills y tools

```mermaid
flowchart TB
    Prompt["Prompt del usuario"] --> Router["AgentRequestRouter"]
    Router --> Normalize["Normalizar acentos + tokenizar"]
    Normalize --> Score["Score por ruta"]

    Score --> Awb["awb-dispatch"]
    Score --> Cold["cold-chain"]
    Score --> Comm["client-comm"]
    Score --> General["operations-general"]
    Score --> Mixed["mixed-operations"]

    Awb --> AwbSkills["Skills: core-domain + awb-dispatch"]
    Cold --> ColdSkills["Skills: core-domain + cold-chain"]
    Comm --> CommSkills["Skills: core-domain + client-comm"]
    General --> GeneralSkills["Skills: core-domain"]
    Mixed --> MixedSkills["Union de skills/tools seleccionados"]

    Awb --> AwbTools["Tools: create_awb, get_awb_status, search_customer, register_customer"]
    Cold --> ColdTools["Tools: get_temperature_requirements, get_awb_status"]
    Comm --> CommTools["Tools: simulate_email, search_customer, register_customer, get_awb_status"]

    Score --> StrongSignals["Senales fuertes: awb, reserva, temperatura, correo"]
    Score --> WeakSignals["Senales debiles: flores, pescado, frutas, mariscos"]
    Score --> Trace["RoutingTrace"]

    Trace --> Confidence["Confidence 0..1"]
    Trace --> Reason["Reason explicable"]
    Trace --> Candidates["Candidates + score + matchedSignals"]
    Trace --> Audit["AgentAuditLog RoutingTraceJson"]
    Trace --> UI["Frontend trace card"]
```

## 6. Sesiones y memoria

```mermaid
flowchart LR
    SessionId["sessionId frontend"] --> Mode{"Modo"}
    Mode --> Fast["fast"]
    Mode --> MemoryMode["memory"]

    Fast --> Transient["Sesion SDK transitoria"]
    Transient --> Delete["Se elimina al terminar turno"]

    MemoryMode --> Active["Sesion SDK activa en memoria"]
    Active --> TTL["TTL 20 minutos"]
    Active --> RouteCheck{"Misma ruta especialista?"}
    RouteCheck -->|Si| Reuse["Reusar sesion"]
    RouteCheck -->|No| Recreate["Eliminar y recrear sesion"]

    MemoryMode --> Structured["ConversationMemory persistida"]
    Structured --> Customer["CustomerJson"]
    Structured --> Shipment["ShipmentJson"]
    Structured --> LastRoute["LastRouteName"]
    Structured --> Intent["CurrentIntent"]
    Structured --> Db["SQLite ConversationMemory"]
```

## 7. Base operativa SQLite/Turso

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

    SimulatedEmails {
        int Id PK
        int ShipmentId FK
        string RecipientEmail
        string Subject
        string Body
        string SentAt
        string Status
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

    SchemaMigrations {
        int Version PK
        string Name
        string AppliedAt
    }
```

## 8. Autoaprendizaje supervisado propuesto

```mermaid
flowchart LR
    Audit["AgentAuditLog"] --> Analysis["Analisis de patrones"]
    Routing["RoutingTrace"] --> Analysis
    Memory["ConversationMemory"] --> Analysis
    Events["ShipmentEvents"] --> Analysis

    Analysis --> Suggestions["LearningSuggestion"]
    Suggestions --> Review["Revision humana"]
    Review -->|Aprobar| Approved["ApprovedKnowledge"]
    Review -->|Rechazar| Rejected["RejectedSuggestion"]

    Approved --> Versioning["Versionado"]
    Versioning --> Skills["Skills"]
    Versioning --> Router["Router/evaluaciones"]
    Versioning --> Workflows["WorkflowState"]
```

## 9. Roadmap de mejora

```mermaid
flowchart TB
    A["Estado actual"] --> B["RoutingTrace implementado"]
    B --> C["Workflows hibridos base"]
    C --> D["WorkflowState por proceso"]
    D --> E["Evaluacion de ruta + campos + tool + respuesta"]
    E --> F["Memoria por entidad"]
    F --> G["Aprendizaje supervisado"]
    G --> H["Turso remoto / integraciones reales"]

    C --> C1["AWB reservation workflow"]
    C --> C2["Customer onboarding workflow"]
    C --> C3["Cold-chain alert workflow"]
    C --> C4["Client notification workflow"]

    F --> F1["LearningSuggestion"]
    F --> F2["ApprovedKnowledge"]
    F --> F3["Skill versioning"]
```
