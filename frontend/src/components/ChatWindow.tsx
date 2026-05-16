import { useState, useRef, useCallback, useEffect } from 'react';

const DEFAULT_AGENT_NAME = 'Code Name Orion';
const COPILOT_NAME = 'Orion';

interface ChatMessage {
  role: 'user' | 'agent' | 'tool';
  content: string;
  id: string;
  timestamp: string;
  trace?: ChatTrace;
  modelLabel?: string;
}

interface ChatTrace {
  sessionId: string;
  prompt: string;
  startedAt: string;
  completedAt: string;
  durationMs: number;
  model: string;
  chatMode?: string;
  routeName?: string;
  routeDisplayName?: string;
  tools: string[];
  skills: string[];
  subagents: string[];
  error?: string;
  usage?: ChatUsageTrace | null;
}

interface ChatUsageTrace {
  inputTokens?: number | null;
  outputTokens?: number | null;
  totalTokens?: number | null;
  cacheReadTokens?: number | null;
  cacheWriteTokens?: number | null;
  cost?: number | null;
  sessionCurrentTokens?: number | null;
  sessionTokenLimit?: number | null;
  messagesInContext?: number | null;
}

interface RuntimeInfo {
  agentName: string;
  model: string;
  defaultMode?: string;
  supportedModes?: string[];
}

interface ChatWindowProps {
  onRefresh: () => void;
}

export default function ChatWindow({ onRefresh }: ChatWindowProps) {
  const [runtime, setRuntime] = useState<RuntimeInfo>({
    agentName: DEFAULT_AGENT_NAME,
    model: 'Cargando modelo...',
    defaultMode: 'memory',
    supportedModes: ['fast', 'memory']
  });
  const [chatMode, setChatMode] = useState<'fast' | 'memory'>('memory');
  const [messages, setMessages] = useState<ChatMessage[]>([
    {
      role: 'agent',
      content: buildWelcomeMessage('Cargando modelo...', 'memory'),
      id: 'welcome',
      timestamp: formatTimestamp(new Date())
    }
  ]);
  const [input, setInput] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const sessionIdRef = useRef<string>(generateSessionId());
  const toolStatusIdRef = useRef<string | null>(null);
  const toolLabelRef = useRef<string | null>(null);
  const agentStatusIdRef = useRef<string | null>(null);

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  };

  useEffect(() => {
    scrollToBottom();
  }, [messages]);

  useEffect(() => {
    let cancelled = false;

    async function loadRuntimeInfo() {
      try {
        const res = await fetch('/api/runtime');
        if (!res.ok) return;

        const data = await res.json() as RuntimeInfo;
        if (!cancelled) {
          const model = data.model || 'Modelo no disponible';
          const agentName = data.agentName || DEFAULT_AGENT_NAME;
          const currentMode = data.defaultMode || 'memory';

          setRuntime({
            agentName,
            model,
            defaultMode: currentMode,
            supportedModes: data.supportedModes || ['fast', 'memory']
          });

          setChatMode((currentMode === 'fast' ? 'fast' : 'memory'));

          setMessages(prev => prev.map(message =>
            message.id === 'welcome'
              ? {
                  ...message,
                  content: buildWelcomeMessage(model, currentMode),
                  modelLabel: model,
                  timestamp: formatTimestamp(new Date())
                }
              : message
          ));
        }
      } catch {
        if (!cancelled) {
          setRuntime(prev => ({ ...prev, model: 'Modelo no disponible' }));
        }
      }
    }

    loadRuntimeInfo();

    return () => {
      cancelled = true;
    };
  }, []);

  function upsertStatusMessage(messageId: string, content: string) {
    setMessages(prev => {
      const exists = prev.some(message => message.id === messageId);
      if (!exists) {
        return [...prev, { role: 'tool', content, id: messageId, timestamp: formatTimestamp(new Date()) }];
      }

      return prev.map(message =>
        message.id === messageId
          ? { ...message, content, timestamp: formatTimestamp(new Date()) }
          : message
      );
    });
  }

  const sendMessage = useCallback(async () => {
    if (!input.trim() || isLoading) return;

    const userMsg = input.trim();
    setInput('');
    toolStatusIdRef.current = null;
    toolLabelRef.current = null;
    agentStatusIdRef.current = null;
    setMessages(prev => [...prev, { role: 'user', content: userMsg, id: crypto.randomUUID(), timestamp: formatTimestamp(new Date()) }]);
    setIsLoading(true);

    const agentMessageId = crypto.randomUUID();
    setMessages(prev => [...prev, { role: 'agent', content: '', id: agentMessageId, timestamp: formatTimestamp(new Date()) }]);

    try {
      const res = await fetch('/api/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ message: userMsg, sessionId: sessionIdRef.current, mode: chatMode })
      });

      if (!res.body) throw new Error('Sin respuesta');

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      let receivedTerminalEvent = false;

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (!line.trim()) continue;
          if (line.startsWith('event:')) continue;
          if (line.startsWith('data:')) {
            try {
              const json = JSON.parse(line.slice(5).trim());
              if (json.type === 'delta' || json.type === 'final') {
                setMessages(prev => {
                  return prev.map(message => {
                    if (message.id !== agentMessageId) {
                      return message;
                    }

                    if (json.type === 'final') {
                      if (!json.content) {
                        return message;
                      }

                      return { ...message, content: json.content, timestamp: formatTimestamp(new Date()) };
                    }

                    return { ...message, content: message.content + (json.content || ''), timestamp: formatTimestamp(new Date()) };
                  });
                });
              }
              if (json.type === 'tool_start') {
                const toolLabel = formatToolLabel(json.tool);
                const messageId: string = toolStatusIdRef.current ?? crypto.randomUUID();
                toolStatusIdRef.current = messageId;
                toolLabelRef.current = toolLabel;
                upsertStatusMessage(messageId, `${COPILOT_NAME} está ejecutando: ${toolLabel}`);
              }
              if (json.type === 'tool_done' && toolStatusIdRef.current) {
                upsertStatusMessage(
                  toolStatusIdRef.current,
                  `Operación completada: ${toolLabelRef.current || 'Operación'}`
                );
              }
              if (json.type === 'subagent_selected') {
                const messageId: string = agentStatusIdRef.current ?? crypto.randomUUID();
                agentStatusIdRef.current = messageId;
                upsertStatusMessage(messageId, `${COPILOT_NAME} delegó la tarea a: ${formatAgentLabel(json.agent)}`);
              }
              if (json.type === 'subagent_started' && agentStatusIdRef.current) {
                upsertStatusMessage(agentStatusIdRef.current, `Agente trabajando: ${formatAgentLabel(json.agent)}`);
              }
              if (json.type === 'subagent_completed' && agentStatusIdRef.current) {
                upsertStatusMessage(agentStatusIdRef.current, `Agente finalizó: ${formatAgentLabel(json.agent)}`);
              }
              if (json.type === 'subagent_failed' && agentStatusIdRef.current) {
                upsertStatusMessage(agentStatusIdRef.current, `Agente con incidencia: ${formatAgentLabel(json.agent)}`);
              }
              if (json.type === 'done') {
                receivedTerminalEvent = true;
                setIsLoading(false);
                onRefresh();
              }
              if (json.type === 'error') {
                receivedTerminalEvent = true;
                setMessages(prev => {
                  return prev.map(message =>
                    message.id === agentMessageId
                      ? { ...message, content: message.content + '\n[Error: ' + (json.content || 'Desconocido') + ']', timestamp: formatTimestamp(new Date()) }
                      : message
                  );
                });
                setIsLoading(false);
              }
              if (json.type === 'trace') {
                const trace = normalizeTrace(json.trace);
                setMessages(prev => {
                  return prev.map(message =>
                    message.id === agentMessageId
                      ? {
                          ...message,
                          trace,
                          modelLabel: trace.model || runtime.model,
                          timestamp: formatTimestamp(new Date())
                        }
                      : message
                  );
                });
              }
            } catch {
              // ignora líneas malformadas
            }
          }
        }
      }

      if (!receivedTerminalEvent) {
        setMessages(prev => prev.map(message =>
          message.id === agentMessageId
            ? {
                ...message,
                content: message.content
                  ? `${message.content}\n\n[La conexión con Orion se interrumpió antes de completar la respuesta. Intenta reenviar el mensaje o iniciar una nueva conversación.]`
                  : 'La conexión con Orion se interrumpió antes de completar la respuesta. Intenta reenviar el mensaje o iniciar una nueva conversación.',
                timestamp: formatTimestamp(new Date())
              }
            : message
        ));
        setIsLoading(false);
      }
    } catch (err) {
      setMessages(prev => [...prev, { role: 'agent', content: `No pude conectar con ${COPILOT_NAME}. Verifica que el backend siga corriendo en local.`, id: crypto.randomUUID(), timestamp: formatTimestamp(new Date()) }]);
      setIsLoading(false);
    }
  }, [chatMode, input, isLoading, onRefresh, runtime.model]);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  };

  const resetConversation = async () => {
    const previousSessionId = sessionIdRef.current;

    try {
      await fetch('/api/chat/reset', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sessionId: previousSessionId })
      });
    } catch {
      // best effort
    }

    sessionIdRef.current = generateSessionId();
    toolStatusIdRef.current = null;
    toolLabelRef.current = null;
    agentStatusIdRef.current = null;
    setInput('');
    setIsLoading(false);
    setMessages([
      {
        role: 'agent',
        content: buildWelcomeMessage(runtime.model, chatMode),
        id: 'welcome',
        timestamp: formatTimestamp(new Date()),
        modelLabel: runtime.model
      }
    ]);
  };

  return (
    <div className="panel" style={{ minHeight: 0 }}>
      <div className="panel-header">Agent</div>
      <div className="runtime-banner">
        <div className="runtime-copy">
          <span className="runtime-title">{runtime.agentName}</span>
          <span className="runtime-subtitle">Copilot operativo para carga perecedera</span>
        </div>
        <div className="runtime-actions">
          <span className="runtime-pill">Modelo: <strong>{runtime.model}</strong></span>
          <div className="mode-selector" role="group" aria-label="Modo de conversación">
            <button
              type="button"
              className={`secondary-button ${chatMode === 'fast' ? 'selected' : ''}`}
              onClick={() => setChatMode('fast')}
              disabled={isLoading}
            >
              Rápido
            </button>
            <button
              type="button"
              className={`secondary-button ${chatMode === 'memory' ? 'selected' : ''}`}
              onClick={() => setChatMode('memory')}
              disabled={isLoading}
            >
              Con memoria
            </button>
          </div>
          <button type="button" className="secondary-button" onClick={resetConversation} disabled={isLoading}>
            Nueva conversación
          </button>
        </div>
      </div>
      <div className="chat-messages">
        {messages.map(m => (
          <div key={m.id} className={`message ${m.role}`}>
            <div>{m.content}</div>
            {m.role === 'agent' && (m.modelLabel || m.trace) && (
              <div className="message-model">
                Modelo: {m.modelLabel || m.trace?.model || runtime.model}
              </div>
            )}
            {m.trace && <TraceCard trace={m.trace} />}
            <div className="message-time">{m.timestamp}</div>
          </div>
        ))}
        {isLoading && messages[messages.length - 1]?.role === 'agent' && messages[messages.length - 1]?.content === '' && (
          <div className="message agent">
            <div><span className="spinner"></span> {COPILOT_NAME} está preparando la respuesta...</div>
            <div className="message-time">{formatTimestamp(new Date())}</div>
          </div>
        )}
        <div ref={messagesEndRef} />
      </div>
      <div className="chat-input-area">
        <input
          type="text"
          placeholder="Escribe lo que necesitas. Ej: crear cliente, reservar AWB o consultar estado"
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={handleKeyDown}
          disabled={isLoading}
          aria-label="Mensaje para Orion"
        />
        <button type="button" onClick={sendMessage} disabled={isLoading}>
          {isLoading ? '...' : 'Enviar'}
        </button>
      </div>
    </div>
  );
}

function TraceCard({ trace }: { trace: ChatTrace }) {
  const durationSeconds = trace.durationMs > 0 ? `${(trace.durationMs / 1000).toFixed(1)} s` : 'n/d';

  return (
    <details className="trace-card">
      <summary>Flujo de ejecución</summary>
      <div className="trace-grid">
        <div><span>Modelo</span><strong>{trace.model || 'No disponible'}</strong></div>
        <div><span>Modo</span><strong>{formatModeLabel(trace.chatMode)}</strong></div>
        <div><span>Ruta</span><strong>{trace.routeDisplayName || 'No disponible'}</strong></div>
        <div><span>Duración</span><strong>{durationSeconds}</strong></div>
        <div><span>Herramientas</span><strong>{formatTraceList(trace.tools, formatToolLabel)}</strong></div>
        <div><span>Skills</span><strong>{formatTraceList(trace.skills)}</strong></div>
        <div><span>Subagentes</span><strong>{formatTraceList(trace.subagents, formatAgentLabel)}</strong></div>
        <div><span>Tokens</span><strong>{formatTokenSummary(trace.usage)}</strong></div>
      </div>
      {trace.usage && (
        <div className="trace-usage">
          <span>Entrada: {formatNumber(trace.usage.inputTokens)}</span>
          <span>Salida: {formatNumber(trace.usage.outputTokens)}</span>
          <span>Total: {formatNumber(trace.usage.totalTokens)}</span>
          <span>Contexto: {formatContextUsage(trace.usage)}</span>
          {trace.usage.cost != null && <span>Costo: {trace.usage.cost.toFixed(6)}</span>}
        </div>
      )}
      {trace.error && <div className="trace-error">Error: {trace.error}</div>}
    </details>
  );
}

function generateSessionId() {
  return Math.random().toString(36).substring(2, 14);
}

function formatToolLabel(toolName?: string) {
  const labels: Record<string, string> = {
    create_awb: 'Creación de AWB',
    get_awb_status: 'Consulta de estado AWB',
    get_temperature_requirements: 'Validación de temperatura',
    register_customer: 'Registro de cliente',
    search_customer: 'Búsqueda de cliente',
    simulate_email: 'Simulación de email'
  };

  return labels[toolName || ''] || toolName || 'Operación';
}

function formatAgentLabel(agentName?: string) {
  const labels: Record<string, string> = {
    'AWB Dispatcher': 'Despachador AWB',
    awb_dispatcher: 'Despachador AWB',
    'Cold Chain Validator': 'Validador de cadena de frío',
    cold_chain_validator: 'Validador de cadena de frío',
    'Client Communications': 'Comunicaciones con clientes',
    client_communications: 'Comunicaciones con clientes',
    'awb-dispatcher': 'Despachador AWB',
    'cold-chain-validator': 'Validador de cadena de frío',
    'client-communications': 'Comunicaciones con clientes'
  };

  return labels[agentName || ''] || agentName || 'Agente';
}

function normalizeTrace(trace: ChatTrace): ChatTrace {
  return {
    ...trace,
    tools: trace.tools || [],
    skills: trace.skills || [],
    subagents: trace.subagents || [],
    usage: trace.usage || null
  };
}

function formatTraceList(values: string[], formatter?: (value?: string) => string) {
  if (!values || values.length === 0) return 'Ninguno';
  return values.map(value => formatter ? formatter(value) : value).join(', ');
}

function formatTokenSummary(usage?: ChatUsageTrace | null) {
  if (!usage) return 'No disponible';
  if (usage.totalTokens != null) return `${Math.round(usage.totalTokens)} tokens`;
  if (usage.inputTokens != null || usage.outputTokens != null) {
    return `${Math.round((usage.inputTokens || 0) + (usage.outputTokens || 0))} tokens`;
  }
  return 'No disponible';
}

function formatContextUsage(usage: ChatUsageTrace) {
  if (usage.sessionCurrentTokens == null && usage.sessionTokenLimit == null) {
    return 'No disponible';
  }

  if (usage.sessionCurrentTokens != null && usage.sessionTokenLimit != null) {
    return `${Math.round(usage.sessionCurrentTokens)} / ${Math.round(usage.sessionTokenLimit)}`;
  }

  return formatNumber(usage.sessionCurrentTokens ?? usage.sessionTokenLimit);
}

function formatNumber(value?: number | null) {
  if (value == null) return 'No disponible';
  return Math.round(value).toLocaleString('es-CO');
}

function formatModeLabel(mode?: string | null) {
  return mode === 'fast' ? 'Rápido' : 'Con memoria';
}

function buildWelcomeMessage(model: string, mode?: string | null) {
  return `Hola, soy ${COPILOT_NAME}, tu copilot agent para AWB, cadena de frio, clientes y seguimiento operativo. Modelo activo: ${model}. Modo actual: ${formatModeLabel(mode)}.`;
}

function formatTimestamp(date: Date) {
  return date.toLocaleTimeString('es-CO', {
    hour: '2-digit',
    minute: '2-digit'
  });
}
