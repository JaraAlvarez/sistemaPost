# Fase 12-C · Pago en línea de las suscripciones (pasarela de pagos) — Propuesta

> Estado: **APROBADA con las recomendaciones** · 2026-10-17 (dijiste: "te encargo todas las funciones que te recomendé").
> Respuestas tomadas mientras no estés: ver la §10.
> Origen: pediste que el cliente pueda comprar y renovar su mensualidad sin depender de que tú registres cada pago a mano.
> Base: [Fase 12-A](fase-12a-propuesta.md) (portal y suscripciones; ahí el cobro en línea quedó como "futuro"),
> [ADR-0039](../adr/0039-modelo-por-edicion-y-licencia-por-nit.md) (ediciones, una licencia por NIT, renovación manual),
> [Fase 16](fase-16-propuesta.md) (rol **Cliente** en el portal) y [doc 09](../09-licenciamiento.md) (la pasarela aparece como "futuro").
> Se numera **12-C** porque amplía el licenciamiento; la Fase 18 ya está reservada para editar maestros desde el portal.

## 0. En pocas palabras

Hoy: el cliente te paga por fuera → tú entras al portal → "Renovar" con la referencia del pago → la tienda se entera en su próximo
reporte diario.

Con esta fase: el cliente oprime **"Pagar mensualidad"** (en su POS, en el portal o en un enlace que le mandes por WhatsApp) → paga
con Nequi, PSE, tarjeta o Bancolombia → **la suscripción se renueva sola** → la tienda se entera en minutos. Tú solo miras la lista de
pagos. **La llave de licencia sigue siendo la misma**; nada cambia para el cliente en su instalación.

La renovación manual que tienes hoy **se conserva** (pagos en efectivo, convenios, cortesías).

## 1. Alcance

| Incluido | Excluido (otra fase o decisión tuya) |
|---|---|
| Tabla de **precios** por edición y periodicidad, editable por ti en el portal | Cobro **automático** con tarjeta guardada (débito recurrente) → etapa 2, ver §6 |
| **Orden de pago** con referencia única y el precio congelado al momento de crearla | **Factura electrónica** de la mensualidad que tú le emites al cliente (ver pregunta 4) |
| Pago en la página de la pasarela (**Wompi Web Checkout**): Nequi, PSE, tarjetas, Botón Bancolombia, Daviplata, efectivo en corresponsales | Cambiar de edición pagando la diferencia (prorrateo): lo sigues haciendo tú desde el portal |
| Confirmación por **evento de la pasarela** (webhook firmado) y **renovación automática** | Devoluciones desde el sistema: se hacen en el panel de Wompi y aquí solo se anotan |
| Enlace de pago **público y seguro** por suscripción (para WhatsApp y para el POS), con código QR | Correos de recordatorio (el portal no envía correos hoy; ver pregunta 8) |
| Botón **"Pagar mensualidad"** en el aviso de vencimiento del POS y en "Mi suscripción" del portal del cliente | Venta a clientes nuevos sin pasar por ti (registro en línea de empresas y llaves) |
| Pantalla **Pagos** en el portal (lista, detalle, conciliación) y tarea que revisa los pagos pendientes | Otras pasarelas: el diseño las permite, pero solo se implementa una |

## 2. Por qué Wompi (recomendación)

| | Wompi (Bancolombia) | ePayco | Mercado Pago | PayU |
|---|---|---|---|---|
| Nequi y Botón Bancolombia | ✅ propios | ✅ | Parcial | ✅ |
| PSE, tarjetas, efectivo | ✅ | ✅ | ✅ | ✅ |
| Ambiente de pruebas (sandbox) | ✅ llaves `pub_test_` / `prv_test_` | ✅ | ✅ | ✅ |
| Eventos firmados y consulta de la transacción | ✅ | ✅ | ✅ | ✅ |
| Tarifa publicada (oct-2026) | 2,65 % + $700 + IVA por pago **exitoso** (QR 1 %) | Similar | Similar | Similar |

Se elige **Wompi** porque la mayoría de tenderos colombianos ya usa Nequi o Bancolombia, el dinero llega a una cuenta Bancolombia o
Nequi, y su integración es sencilla (página de pago alojada por ellos: **nunca** tocamos datos de tarjetas). Las tarifas cambian:
confírmalas al abrir tu cuenta. El código queda detrás de una interfaz (`IPaymentGateway`), así que cambiar de pasarela después es
escribir un adaptador nuevo, no rehacer la fase.

## 3. Decisiones

| # | Decisión | Motivo |
|---|---|---|
| D12C-01 | Pasarela **Wompi** con su **Web Checkout** (redirección a la página de Wompi), detrás de `IPaymentGateway` | El cliente paga en una página de Wompi; nosotros nunca vemos ni guardamos datos de tarjetas ni de cuentas (sin obligaciones PCI) |
| D12C-02 | **Precios** en la nube: valor en pesos **con IVA incluido** por edición (Caja Única / Multicaja) y periodicidad (mensual / anual), con fecha "vigente desde" e historial. Solo el superadministrador los cambia, y queda en la auditoría | Un cambio de precio no se hace en el código ni afecta pagos ya iniciados |
| D12C-03 | **Orden de pago** (`payment_orders`): suscripción, periodos, valor congelado, referencia única `BP-<año>-<consecutivo>`, estado `PENDING` → `APPROVED` / `DECLINED` / `VOIDED` / `ERROR` / `EXPIRED` (sin respuesta en 24 h ⚙️) | Cada intento queda registrado; la referencia une el pago de Wompi con la renovación |
| D12C-04 | **La única fuente de verdad es el evento firmado de Wompi**, confirmado además consultando la transacción con la llave privada. La página a la que vuelve el cliente **solo muestra** el estado; nunca renueva | Alguien podría falsificar la página de regreso; no puede falsificar la firma del evento |
| D12C-05 | Al aprobarse: se verifica que **valor y moneda** coincidan con la orden y se ejecuta la **misma renovación** que haces hoy a mano (`Renew`), con la referencia de Wompi. **Una orden renueva una sola vez** (restricción única en la BD): los eventos repetidos no renuevan dos veces | Se reutiliza la regla ya probada (sumar el periodo al final del vigente, o empezar hoy si estaba vencida) |
| D12C-06 | Si el valor no coincide o la suscripción no admite renovación (**suspendida o cancelada por ti**), el pago queda **"Requiere revisión"** con alerta en el tablero; **no** se renueva solo | Una suspensión tuya (fraude, disputa) no debe deshacerse con un pago automático |
| D12C-07 | **Enlace de pago**: `https://<tu-dominio>/pagar/<código>` con un código aleatorio de 128 bits por suscripción (se puede regenerar). La página pública muestra **solo** el nombre comercial, la edición, el vencimiento y el valor; no muestra NIT, llave, ventas ni nada más | Sirve para WhatsApp y para el POS sin pedir usuario y contraseña, sin exponer datos |
| D12C-08 | **En el POS**: el reporte diario (check-in) devuelve el enlace de pago cuando faltan **15 días ⚙️** o menos, o la licencia está en gracia o restringida. La administración muestra el aviso con el botón **"Pagar mensualidad"** y un **QR** para pagar desde el celular. Tras pagar, el POS consulta la licencia de inmediato | El dueño del supermercado paga desde donde ya está, sin entrar al portal |
| D12C-09 | **Portal del cliente** (rol Cliente, Fase 16): página "Mi suscripción" con vencimiento, historial de pagos y botón "Pagar" | Para quien prefiera entrar al portal |
| D12C-10 | **Conciliación**: una tarea cada 15 min ⚙️ consulta en Wompi las órdenes `PENDING` (PSE y efectivo pueden tardar horas) y aplica el mismo proceso de D12C-05. La pantalla **Pagos** permite "Consultar ahora" una orden | Si un evento de Wompi se pierde, el pago igual se aplica |
| D12C-11 | **Llaves de Wompi** (pública, privada, de integridad y de eventos) solo en variables de entorno del servidor (`Payments__Wompi__…`). **Nunca** en el repositorio ni en la BD. En desarrollo, las llaves de prueba van en *user-secrets* | Igual que la clave de firma de licencias |
| D12C-12 | Todo queda en la **auditoría** de la nube: orden creada, evento recibido (válido o rechazado), pago aprobado, renovación automática, revisión manual, cambio de precio | Trazabilidad ante un reclamo ("yo pagué y no se renovó") |
| D12C-13 | El endpoint de eventos (`POST /v1/payments/wompi/events`) tiene límite de peticiones, rechaza firmas inválidas con 401 (y lo audita) y responde 200 rápido; el proceso pesado va en segundo plano | Wompi reintenta si no recibe respuesta; un atacante no debe poder saturarlo |

## 4. Cómo queda el flujo

```
Cliente (POS, portal o WhatsApp)
   │  "Pagar mensualidad"
   ▼
/pagar/<código>  ── crea la orden BP-2026-000123 ($ valor congelado)
   │  redirección con firma de integridad (referencia + valor + moneda)
   ▼
Página de Wompi (Nequi, PSE, tarjeta…)  ──►  el cliente paga
   │                                         │
   │ regreso: /pagar/resultado                │ evento firmado (webhook)
   │ (solo muestra el estado)                 ▼
   │                               Nube: verifica la firma → consulta la transacción
   │                                     → valor y moneda correctos → Renew(...)
   │                                     → auditoría
   ▼
POS: "Verificando…" → check-in inmediato → licencia con la nueva fecha
```

## 5. Qué cambia en cada parte

| Parte | Cambios |
|---|---|
| **Nube (licencias)** | Tablas `prices`, `payment_orders`, `payment_events` (solo inserción) y `payment_links`; adaptador Wompi; endpoints públicos `/pagar/…` y de eventos; tarea de conciliación; migración de esquema |
| **Portal (tú)** | Pantallas **Precios** y **Pagos** (lista con filtros, detalle con los eventos recibidos, "Consultar ahora", "Marcar revisado"); en la suscripción: historial de pagos, "Copiar enlace de pago" y QR; alerta en el tablero para pagos que requieren revisión |
| **Portal (cliente)** | Página "Mi suscripción" con el botón "Pagar" |
| **POS** | El check-in guarda el enlace de pago; el aviso de vencimiento muestra "Pagar mensualidad" y el QR; botón "Ya pagué, verificar" que hace el check-in al instante |
| **Documentación** | ADR nuevo (pagos en línea), actualización del doc 09, guía para abrir la cuenta de Wompi y configurar las llaves en el VPS |

## 6. Etapa 2 (no incluida): cobro automático

Wompi permite guardar una tarjeta o una cuenta Nequi (con autorización del cliente) y cobrar cada mes sin que el cliente haga nada. Se
deja para después porque exige: consentimiento explícito y forma de retirarlo, reintentos cuando el cobro falla, avisos antes de cada
cobro, y más cuidado con las disputas. Primero conviene ver cómo responden los clientes al pago con un clic.

## 7. Lo que tienes que hacer tú (yo no puedo)

1. **Abrir la cuenta de comercio en Wompi** (registro.wompi.co) con tu RUT y la cuenta Bancolombia o Nequi donde recibirás el dinero.
   Yo no creo cuentas ni ingreso con credenciales de servicios externos.
2. Copiar las **llaves de prueba** (sandbox) a tu equipo con `dotnet user-secrets` (te dejo el comando exacto en la guía). Nunca me las
   pegues en el chat ni las subas al repositorio.
3. En el panel de Wompi, registrar la **URL de eventos** de tu servidor (cuando esté publicado en el VPS).
4. Definir los **precios** (pregunta 2).

## 8. Validación

- **Pruebas automáticas (yo):** verificación de la firma de eventos con el ejemplo oficial de Wompi; firma de integridad; estados de la
  orden; evento repetido que no renueva dos veces; valor alterado que no renueva; suscripción suspendida que queda en revisión;
  conciliación de órdenes pendientes; enlace de pago que no muestra datos sensibles; pasarela simulada de punta a punta.
- **Prueba en el sandbox (tú):** pagar con las tarjetas y el Nequi de prueba de Wompi (aprobado, rechazado, pendiente) y ver la
  suscripción renovada, el pago en la lista y el aviso desaparecer del POS.

## 9. Preguntas para aprobar

1. **Pasarela:** ¿Wompi está bien, o ya tienes cuenta en otra (ePayco, Mercado Pago, PayU)?
2. **Precios:** ¿cuánto vale cada edición mensual y anual? ¿El anual tiene descuento? ¿Los valores incluyen IVA?
3. **Quién recibe el dinero:** ¿vendes como persona natural o como empresa? (cambia el registro en Wompi; el QR del 1 % es solo para
   personas naturales).
4. **Factura de la mensualidad:** ¿con qué emites hoy tu factura electrónica al cliente? Recomendación: por ahora la sigues emitiendo
   tú desde la lista de pagos; automatizarla sería otra fase.
5. **Varios meses de una vez:** ¿el cliente puede pagar 3, 6 o 12 meses en un solo pago? (recomendación: sí, mensual en 1/3/6 y anual).
6. **Suspendidas:** ¿confirmas que una suscripción suspendida por ti **no** se reactiva sola al pagar? (recomendación: sí, queda en
   revisión).
7. **Cobro automático (§6):** ¿lo dejamos para una etapa 2? (recomendación: sí).
8. **Recordatorios por correo o WhatsApp:** el portal hoy no envía mensajes. ¿Basta con el aviso en el POS y el enlace que tú envías, o
   quieres que una fase posterior agregue correos automáticos?

## 10. Respuestas tomadas con las recomendaciones (puedes cambiarlas)

| Pregunta | Respuesta aplicada |
|---|---|
| 1 · Pasarela | **Wompi**, detrás de `IPaymentGateway` (cambiar después es un adaptador nuevo) |
| 2 · Precios | **No se inventan precios**: la tabla nace vacía y tú los cargas en el portal (Precios). Mientras una edición no tenga precio, su enlace de pago dice "Precio no configurado: comuníquese con BusinessPost" y no crea órdenes |
| 3 · Quién recibe | No cambia el código: lo define tu registro en Wompi |
| 4 · Factura de la mensualidad | La sigues emitiendo tú; la lista de pagos muestra lo necesario (empresa, NIT, valor, fecha, referencia) |
| 5 · Varios meses | Sí: mensual en 1, 3 o 6 periodos; anual en 1 |
| 6 · Suspendidas | No se reactivan solas: el pago queda **"Requiere revisión"** |
| 7 · Cobro automático | Etapa 2 (no se implementa ahora) |
| 8 · Recordatorios | Aviso en el POS y enlace por WhatsApp; correos en una fase posterior |

**Pasarela apagada por defecto:** sin las llaves de Wompi configuradas, el portal muestra "Pago en línea no configurado" y todo
funciona como hoy (renovación manual). Así la versión se puede publicar sin riesgo antes de que abras tu cuenta.
