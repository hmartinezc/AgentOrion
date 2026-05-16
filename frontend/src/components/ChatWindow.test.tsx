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
