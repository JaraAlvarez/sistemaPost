# ADR-0063 · Pago en línea de suscripciones con Wompi

- **Estado:** Aceptada · 2026-10-17 · Nube (licenciamiento) · Fase 12-C ([propuesta](../fases/fase-12c-propuesta.md), aprobada con
  las recomendaciones de su §10). Amplía [ADR-0039](0039-modelo-por-edicion-y-licencia-por-nit.md) (la renovación manual se conserva).

## Contexto
Hasta la Fase 12-C cada renovación la registraba el propietario a mano en el portal ("Renovar" con la referencia del pago). Se quiere
que el cliente pague su mensualidad en línea (Nequi, PSE, tarjeta, Botón Bancolombia) y que la suscripción se renueve sola, sin
tocar nunca datos de tarjetas ni de cuentas, y sin que un pago pueda renovar dos veces ni reactivar una suscripción suspendida.

## Decisión
- **Pasarela Wompi con Web Checkout** detrás de `IPaymentGateway` (adaptador `WompiGateway`; cambiar de pasarela es otro adaptador).
  El cliente paga en la página de Wompi: BusinessPost no ve ni guarda datos de pago (sin obligaciones PCI).
- **Precios** (`licensing.prices`): pesos con IVA incluido por edición y periodicidad, con "vigente desde"; historial de **solo
  inserción** (vigente = el de mayor `valid_from` ya empezado). Solo el superadministrador los fija (`licensing.price.manage`),
  auditado. **La tabla nace vacía**: sin precio, el enlace dice "Precio no configurado: comuníquese con BusinessPost".
- **Enlace de pago** (`licensing.payment_links`): `/pagar/<código>` con 128 bits aleatorios en base64url, uno vigente por
  suscripción, regenerable (el anterior deja de funcionar). La página pública muestra solo la razón social, la edición, el
  vencimiento y los valores; nunca el NIT, la clave ni datos de ventas.
- **Orden de pago** (`licensing.payment_orders`): referencia `BP-<año>-<consecutivo de 6 dígitos>` (secuencia de la BD), valor
  **congelado** (precio × periodos: mensual 1, 3 o 6; anual 1), estados `PENDING` → `APPROVED` / `DECLINED` / `VOIDED` / `ERROR` /
  `EXPIRED` (24 h ⚙️) / `REVIEW_REQUIRED`. Suspendida o cancelada no crea órdenes. Máximo 10 órdenes pendientes por suscripción en
  24 h (el enlace es público).
- **Fuente de verdad (D12C-04):** el evento `transaction.updated` firmado (`POST /v1/payments/wompi/events`, anónimo, con límite por
  IP). Se verifica el checksum (SHA-256 de los valores de `signature.properties` + `timestamp` + secreto de eventos, comparación en
  tiempo constante sin distinguir mayúsculas; también se acepta la cabecera `X-Event-Checksum`), se registra en
  `licensing.payment_events` (**solo inserción**, también los rechazados) y la transacción se **confirma consultándola** con la llave
  privada (`GET /v1/transactions/{id}`). Firma inválida → 401 y auditoría `PAYMENT_EVENT_REJECTED`. La página de regreso
  (`/pagar/resultado`) solo lee la orden; nunca renueva.
- **Renovación (D12C-05):** valor en centavos y moneda iguales a la orden → la **misma regla** de la renovación manual
  (`Subscription.Renew`) con la referencia `WOMPI <transacción> <referencia>`, hecha por el usuario técnico. Una orden renueva una
  sola vez: el dominio lo verifica, la fila de la orden se bloquea (`FOR UPDATE`) y la BD lo garantiza con
  `ux_payment_orders__renewal_event` y el índice único `ux_subscription_events__online_payment` (referencia `WOMPI …` de los
  eventos `RENEWED`). Eventos repetidos → sin cambios.
- **Revisión (D12C-06):** valor o moneda distintos, suscripción suspendida o cancelada, edición cambiada, o anulación después de
  renovar → `REVIEW_REQUIRED` con alerta en el tablero; el propietario resuelve y usa "Marcar revisado" con una nota (auditado).
- **Conciliación (D12C-10):** tarea del host cada 15 min ⚙️ (`Payments:ReconcileMinutes`) que consulta en Wompi las órdenes
  pendientes por referencia (`GET /v1/transactions?reference=…`), aplica el mismo proceso y vence las de más de 24 h ⚙️. La pantalla
  Pagos tiene "Consultar ahora".
- **Check-in del POS (D12C-08):** `LicenseTokenResponse.PaymentUrl` (no firmado: es solo un enlace) cuando el pago está configurado
  y la suscripción está en prueba o vigente con 15 días ⚙️ o menos, en gracia o vencida; nunca suspendida ni cancelada.
- **Llaves (D12C-11):** `Payments__Wompi__PublicKey`, `PrivateKey`, `IntegritySecret`, `EventsSecret` y `Environment`
  (`Sandbox`/`Production`) solo en variables de entorno o `dotnet user-secrets`; nunca en el repositorio ni en la BD. **Sin las
  cuatro llaves el pago en línea queda apagado** ("Pago en línea no configurado") y todo funciona como antes.
- **Portal:** pantallas Precios y Pagos (`licensing.payment.view`; "Marcar revisado" y regenerar enlace con
  `licensing.payment.manage`), pestaña "Pagos en línea" de la empresa (historial, "Copiar enlace de pago" y QR) y "Mi suscripción"
  para el rol Cliente (`licensing.subscription.self`, solo las empresas de su cuenta).
- **CSP:** solo en `/pagar` el `form-action` admite además el origen del checkout (`https://checkout.wompi.co`), porque "Pagar"
  redirige allí después de crear la orden.

## Consecuencias
- El cobro deja de depender del propietario para cada pago; la renovación manual sigue disponible (efectivo, convenios).
- Nuevas tablas en `licensing` (migración `V2026.10.009__licensing__online_payments.sql`) y nuevos permisos del portal.
- `payment_orders.renewal_event_id` no tiene clave foránea hacia `subscription_events` a propósito: esa tabla es de solo inserción
  y una FK haría que `TRUNCATE` fallara por otra causa antes que el disparador; la unicidad sí está garantizada.
- El ejemplo de checksum de la documentación de Wompi no coincide con el SHA-256 de su propia cadena de ejemplo; las pruebas usan
  vectores calculados aparte con el algoritmo documentado. Se debe validar con el sandbox (prueba del propietario, §8).
- Etapa 2 (cobro recurrente con tarjeta guardada), factura electrónica de la mensualidad y correos de recordatorio quedan fuera.
