# Microsoft Agent Framework en AgentOrion

## ¿Vale la pena para AgentOrion?

Si, pero **no es necesario para la v1**.

Para la primera version, el `Copilot SDK` directo ya cubre lo necesario:

- BYOK
- Tools
- Skills
- Custom agents dentro de una misma sesion
- Streaming SSE

`Microsoft Agent Framework (MAF)` empieza a valer mucho la pena cuando quieras orquestar varios agentes especializados o mezclar proveedores distintos en un mismo flujo.

## Caso concreto para AgentOrion

### Flujo: reserva de carga perecedera con validacion previa

Objetivo: antes de crear el AWB, pasar por validaciones especializadas y luego generar la comunicacion al cliente.

Agentes:

1. `request-intake`
   Recibe el pedido del cliente y extrae producto, peso, origen, destino y fecha.
2. `cold-chain-validator`
   Revisa temperatura requerida, riesgos de cadena de frio y alertas operativas.
3. `shipment-compliance`
   Verifica si faltan datos clave: certificado fitosanitario, aeropuerto, tipo de producto, restricciones.
4. `awb-dispatcher`
   Crea el AWB y registra el evento.
5. `client-communications`
   Redacta el mensaje de confirmacion o de datos faltantes.

### Orquestacion sugerida

#### Opcion 1: secuencial

1. `request-intake`
2. `cold-chain-validator`
3. `shipment-compliance`
4. Si todo esta OK: `awb-dispatcher`
5. `client-communications`

#### Opcion 2: concurrente

1. `request-intake`
2. `cold-chain-validator` y `shipment-compliance` en paralelo
3. Agente agregador decide si crear AWB o pedir informacion faltante
4. `client-communications`

## Ejemplo de caso de negocio

Prompt del usuario:

```text
Necesito reservar 280kg de rosas desde BOG hacia AMS para manana. El cliente es Flores del Norte y su correo es trafico@floresnorte.com.
```

Resultado esperado del flujo multi-agente:

1. `request-intake` estructura el pedido.
2. `cold-chain-validator` confirma rango de 1C a 3C.
3. `shipment-compliance` detecta si falta certificado o fecha exacta.
4. `awb-dispatcher` crea el AWB si todo esta completo.
5. `client-communications` genera un correo de confirmacion o una solicitud de informacion faltante.

## Recomendacion practica

### Para v1

Mantener la app como esta, con `Copilot SDK` directo y luego agregar `CustomAgents` nativos del SDK.

### Para v2

Evaluar `MAF` si necesitas alguno de estos escenarios:

- mezclar un agente Copilot con otro de Azure OpenAI o Anthropic
- flujos secuenciales o paralelos mas formales
- handoffs entre agentes con politicas explicitas
- trazabilidad de ejecucion entre agentes

## Siguiente paso recomendado

Antes de meter `MAF`, conviene implementar primero estos `CustomAgents` nativos del SDK dentro de AgentOrion:

- `cold-chain-validator`
- `shipment-compliance`
- `awb-dispatcher`
- `client-communications`

Con eso validas el valor del flujo multi-agente sin sumar otra capa de complejidad a la v1.
