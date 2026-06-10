import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import ChatWindow from './ChatWindow'

describe('ChatWindow', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('shows Orion welcome copy from runtime info', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)

      if (url === '/api/runtime') {
        return new Response(JSON.stringify({
          agentName: 'Code Name Orion',
          model: 'qwen3.5-plus',
          defaultMode: 'memory',
          supportedModes: ['fast', 'memory']
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }

      throw new Error(`Unexpected fetch: ${url}`)
    }))

    render(<ChatWindow onRefresh={() => undefined} />)

    await waitFor(() => {
      expect(screen.getByText(/soy Orion, tu copilot agent/i)).toBeInTheDocument()
      expect(screen.getAllByText(/modelo activo: qwen3.5-plus/i).length).toBeGreaterThan(0)
    })
  })

  it('resets the conversation through the reset endpoint', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)

      if (url === '/api/runtime') {
        return new Response(JSON.stringify({
          agentName: 'Code Name Orion',
          model: 'qwen3.5-plus',
          defaultMode: 'memory',
          supportedModes: ['fast', 'memory']
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }

      if (url === '/api/chat/reset') {
        expect(init?.method).toBe('POST')
        const body = JSON.parse(String(init?.body))
        expect(body.sessionId).toBeTruthy()

        return new Response(null, { status: 200 })
      }

      throw new Error(`Unexpected fetch: ${url}`)
    })

    vi.stubGlobal('fetch', fetchMock)

    render(<ChatWindow onRefresh={() => undefined} />)

    await waitFor(() => {
      expect(screen.getAllByText(/Code Name Orion/i).length).toBeGreaterThan(0)
    })

    fireEvent.click(screen.getByRole('button', { name: /nueva conversación/i }))

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith('/api/chat/reset', expect.objectContaining({ method: 'POST' }))
    })

    expect(screen.getByText(/soy Orion, tu copilot agent/i)).toBeInTheDocument()
  })

  it('streams a response and keeps memory mode selected', async () => {
    const streamBody = [
      'data: {"type":"delta","content":"Cliente registrado"}\n\n',
      'data: {"type":"final","content":"Cliente registrado correctamente."}\n\n',
      'data: {"type":"trace","trace":{"sessionId":"abc123","prompt":"registralo","startedAt":"2026-05-16T00:00:00Z","completedAt":"2026-05-16T00:00:02Z","durationMs":2000,"model":"qwen3.5-plus","chatMode":"memory","routeName":"client-comm","routeDisplayName":"Comunicaciones","tools":["register_customer"],"skills":[],"subagents":[],"usage":{"inputTokens":10,"outputTokens":20,"totalTokens":30}}}\n\n',
      'data: {"type":"done"}\n\n'
    ]

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)

      if (url === '/api/runtime') {
        return new Response(JSON.stringify({
          agentName: 'Code Name Orion',
          model: 'qwen3.5-plus',
          defaultMode: 'memory',
          supportedModes: ['fast', 'memory']
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }

      if (url === '/api/chat') {
        const body = JSON.parse(String(init?.body))
        expect(body.mode).toBe('memory')

        return new Response(new ReadableStream({
          start(controller) {
            const encoder = new TextEncoder()
            for (const chunk of streamBody) {
              controller.enqueue(encoder.encode(chunk))
            }
            controller.close()
          }
        }), { status: 200 })
      }

      throw new Error(`Unexpected fetch: ${url}`)
    })

    vi.stubGlobal('fetch', fetchMock)

    render(<ChatWindow onRefresh={() => undefined} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /con memoria/i })).toHaveClass('selected')
    })

    fireEvent.change(screen.getByRole('textbox', { name: /mensaje para orion/i }), {
      target: { value: 'Registra este cliente' }
    })

    fireEvent.click(screen.getByRole('button', { name: /enviar/i }))

    await waitFor(() => {
      expect(screen.getByText(/Cliente registrado correctamente/i)).toBeInTheDocument()
      expect(screen.getByText(/Flujo de ejecución/i)).toBeInTheDocument()
    })
  })

  it('renders enriched trace metadata and tool execution sensitivity', async () => {
    const streamBody = [
      'data: {"type":"tool_start","tool":"cancel_awb"}\n\n',
      'data: {"type":"tool_done","tool":"cancel_awb","success":false,"error":"conflict"}\n\n',
      'data: {"type":"final","content":"No se pudo cancelar el AWB."}\n\n',
      'data: {"type":"trace","trace":{"sessionId":"abc123","prompt":"cancelar awb","startedAt":"2026-05-16T00:00:00Z","completedAt":"2026-05-16T00:00:02Z","durationMs":2000,"model":"qwen3.5-plus","chatMode":"memory","routeName":"awb-dispatch","routeDisplayName":"Despacho AWB","routing":{"selectedRouteName":"awb-dispatch","selectedRouteDisplayName":"Despacho AWB","confidence":0.45,"routingMode":"rules-low-confidence","requiresMiniRouterReview":true,"reason":"No hubo señales suficientes.","candidates":[{"routeName":"awb-dispatch","displayName":"Despacho AWB","score":2,"matchedSignals":[],"selected":true}]},"tools":["cancel_awb"],"toolExecutions":[{"toolCallId":"call-1","toolName":"cancel_awb","requiresConfirmation":true,"success":false,"error":"conflict","resultPreview":"{\\"error\\":\\"conflict\\"}","durationMs":32}],"skills":["awb-dispatch"],"subagents":[],"usage":{"inputTokens":10,"outputTokens":20,"totalTokens":30}}}\n\n',
      'data: {"type":"done"}\n\n'
    ]

    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)

      if (url === '/api/runtime') {
        return new Response(JSON.stringify({
          agentName: 'Code Name Orion',
          model: 'qwen3.5-plus',
          defaultMode: 'memory',
          supportedModes: ['fast', 'memory'],
          providerConfigured: true,
          skillCount: 4,
          routeCount: 4
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }

      if (url === '/api/chat') {
        const body = JSON.parse(String(init?.body))
        expect(body.mode).toBe('memory')

        return new Response(new ReadableStream({
          start(controller) {
            const encoder = new TextEncoder()
            for (const chunk of streamBody) {
              controller.enqueue(encoder.encode(chunk))
            }
            controller.close()
          }
        }), { status: 200 })
      }

      throw new Error(`Unexpected fetch: ${url}`)
    })

    vi.stubGlobal('fetch', fetchMock)

    render(<ChatWindow onRefresh={() => undefined} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /con memoria/i })).toHaveClass('selected')
    })

    fireEvent.change(screen.getByRole('textbox', { name: /mensaje para orion/i }), {
      target: { value: 'Cancela este AWB' }
    })

    fireEvent.click(screen.getByRole('button', { name: /enviar/i }))

    await waitFor(() => {
      expect(screen.getByText(/No se pudo cancelar el AWB/i)).toBeInTheDocument()
      expect(screen.getByText(/Operación completada: Cancelación de AWB/i)).toBeInTheDocument()
    })

    fireEvent.click(screen.getByText(/Flujo de ejecución/i))

    await waitFor(() => {
      expect(screen.getByText(/1 total, 1 con incidencia/i)).toBeInTheDocument()
      expect(screen.getByText(/rules-low-confidence/i)).toBeInTheDocument()
      expect(screen.getByText(/Revisión sugerida/i)).toBeInTheDocument()
      expect(screen.getAllByText((_, element) =>
        element?.textContent === 'Cancelación de AWB · falló, sensible: conflict'
      ).length).toBeGreaterThan(0)
      expect(screen.getByText(/30 tokens/i)).toBeInTheDocument()
    })
  })

  it('shows a fallback message when the stream ends without done', async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)

      if (url === '/api/runtime') {
        return new Response(JSON.stringify({
          agentName: 'Code Name Orion',
          model: 'qwen3.5-plus',
          defaultMode: 'memory',
          supportedModes: ['fast', 'memory']
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }

      if (url === '/api/chat') {
        const body = JSON.parse(String(init?.body))
        expect(body.mode).toBe('memory')

        return new Response(new ReadableStream({
          start(controller) {
            const encoder = new TextEncoder()
            controller.enqueue(encoder.encode('data: {"type":"delta","content":"Buscando cliente..."}\n\n'))
            controller.close()
          }
        }), { status: 200 })
      }

      throw new Error(`Unexpected fetch: ${url}`)
    })

    vi.stubGlobal('fetch', fetchMock)

    render(<ChatWindow onRefresh={() => undefined} />)

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /con memoria/i })).toHaveClass('selected')
    })

    fireEvent.change(screen.getByRole('textbox', { name: /mensaje para orion/i }), {
      target: { value: 'quiero crear una nueva reserva para el cliente jaf' }
    })

    fireEvent.click(screen.getByRole('button', { name: /enviar/i }))

    await waitFor(() => {
      expect(screen.getByText(/Buscando cliente/i)).toBeInTheDocument()
      expect(screen.getByText(/la conexión con Orion se interrumpió antes de completar la respuesta/i)).toBeInTheDocument()
    })
  })
})
