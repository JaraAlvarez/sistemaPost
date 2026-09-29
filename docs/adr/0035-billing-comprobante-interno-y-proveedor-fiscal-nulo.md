# ADR-0035 · Billing con comprobante interno y proveedor fiscal nulo hasta la Fase 11-B

- **Estado:** Aceptada · 2026-09-29 · Fase 7 · Decisión D7-12 (pregunta 1 del propietario; resolución §15.1)

## Contexto
Por ahora el propietario no usa facturación electrónica: entrega el tiquete de venta impreso con los datos de la empresa. La
integración con Factus llega en la Fase 11-B y no debe obligar a cambiar ventas. Una venta nunca puede esperar a un proveedor externo.

## Decisión
- Módulo `Billing` (esquema `billing`, migración `V2026.10.018`): `fiscal_documents` (origen `SALE`, `SALE_VOID`, `CUSTOMER_RETURN`;
  tipo `INTERNAL_RECEIPT` hoy, `POS_ELECTRONIC`/`INVOICE_ELECTRONIC`/`CREDIT_NOTE` en 11-B), `fiscal_document_events` de solo inserción
  y `fiscal_numbering_ranges` (vacía hasta 11-B).
- Ventas emite el documento con `IBillingService` **en su propia transacción**: hoy un **comprobante interno** en estado
  `NOT_REQUIRED` (CHECK: un interno solo puede estar `NOT_REQUIRED` o `VOIDED`); anular la venta anula el comprobante (si el electrónico
  ya estuviera aceptado, se emitiría una nota crédito pendiente).
- `IFiscalProvider` con `NullFiscalProvider` registrado; el envío lo hace un servicio en segundo plano a partir del outbox, nunca en
  línea. El setting `billing.electronic_enabled` (falso) lo enciende en 11-B. API: `GET /billing/documents`, detalle y `retry`.
- El tiquete dice "No es factura".

## Consecuencias
- ✅ La venta opera sin Internet; encender Factus solo agrega el adaptador.
- ⚠️ Riesgo normativo (R-01): según la DIAN el tiquete POS debe ser documento equivalente electrónico para la mayoría de los
  contribuyentes; el propietario debe confirmarlo con su contador antes de operar en una tienda real.
