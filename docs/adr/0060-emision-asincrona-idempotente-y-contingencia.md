# ADR-0060 · Emisión asíncrona, idempotencia por `reference_code` y contingencia sin Internet

- **Estado:** Aceptada · 2026-09-30 · Fase 11-B · Decisiones D11B-02, D11B-03, D11B-04, D11B-05, D11B-09 y D11B-10 de la
  [propuesta](../fases/fase-11b-propuesta.md)
- **Mantiene:** [ADR-0035](0035-billing-comprobante-interno-y-proveedor-fiscal-nulo.md) (la venta nunca espera a un proveedor externo)

## Contexto
Factus asigna el número y el CUFE **en línea** (≈2,5 s por factura), es idempotente por `reference_code`, limita a **80 peticiones por
minuto por NIT**, no tiene webhooks y, si la DIAN rechaza un documento, **ese documento queda en Factus y bloquea los siguientes envíos**
hasta que se elimine. Una caja no puede depender de Internet ni de un tercero.

## Decisión
1. **Documento `PENDING` en la transacción de la venta** (o de la anulación, el cambio, la garantía o la compra) + un mensaje LOCAL del
   outbox que despierta la cola al confirmar. La venta se completa con su número interno aunque no haya Internet.
2. **Cola de envío** (`FiscalQueueRunner` + `FiscalQueueWorker`): cada ⚙️ 5 s o al recibir una señal (venta nueva, reintento, tiquete
   esperando) toma los documentos vencidos **en orden de llegada** (`ix_fiscal_documents__queue`), cada uno en su propio alcance (uno que
   falle no detiene a los demás). El documento se **reclama** (`SUBMITTING`) por un tiempo; si el proceso se cae, al vencer se retoma y
   primero **se consulta el estado** en el proveedor antes de reenviar. `POST /billing/queue/process` hace lo mismo a pedido.
3. **Idempotencia:** `reference_code` ÚNICO por documento = id del origen sin guiones (factura), `NCA…` (nota crédito de anulación), `NCD…`
   (nota crédito de cambio o garantía), `DS…` (documento soporte) y `NAS…` (nota de ajuste). Reenviar el mismo código nunca crea
   otro documento: Factus devuelve el existente (duplicado 200) o un 409 que se resuelve consultando por el código.
4. **Resultados** (neutros, `FiscalOutcome`): aceptado → número, CUFE/CUDE, QR, PDF y fecha de validación; **sin red o tiempo agotado →
   `CONTINGENCY`** (reintento con espera creciente de hasta 15 min y la pasada se detiene para no martillar); error transitorio (429, 5xx,
   token, DIAN sin responder) → `ERROR` con espera 1, 2, 4… hasta 60 min; **rechazo** (regla FAK…, validación 422) → `REJECTED` con el
   mensaje y alerta CRÍTICA en la auditoría.
5. **Contingencia:** sin Internet los documentos quedan `PENDING`/`CONTINGENCY` y salen en orden al volver la conexión; alerta si hay
   pendientes de más de ⚙️ 24 h (`billing.pending_alert_hours`). El tratamiento legal (plazo de transmisión) lo valida el contador.
6. **Ritmo propio** (`FiscalRateLimiter`): ventana deslizante de ⚙️ 60 envíos por minuto (`billing.rate_per_minute`; Factus permite 80). Un
   **HTTP 429** con `Retry-After` pausa **toda** la cola (pausa global) hasta esa hora, no solo el documento.
7. **Rangos** (`fiscal_numbering_ranges`): sincronizados desde Factus (manual y cada ⚙️ 24 h), asignados a una sucursal y opcionalmente a
   una caja por tipo de documento; se elige primero el de la caja, luego el de la sucursal. Sin rango vigente el documento sigue
   pendiente con alerta CRÍTICA (`FISCAL_RANGE_MISSING`) y la venta no se afecta. Alertas al ⚙️ 90 % de uso y a ⚙️ 30 días del vencimiento.
8. **Notas crédito y notas de ajuste esperan** a que su factura o documento soporte esté aceptado. Anular una venta cuya factura aún no fue
   aceptada la **cancela** (`CANCELLED`) antes de enviarla; si ya fue aceptada o está en envío, se emite la nota crédito.
9. **Rechazo que bloquea en Factus → borrar y reenviar:** el supervisor corrige el comprador (`PUT /billing/documents/{id}/buyer`) o
   reintenta (`POST …/retry`); en el reenvío (`Resubmission`) el adaptador **elimina en Factus el documento no validado con el mismo
   `reference_code`** y lo envía de nuevo. Un bloqueo causado por **otro** documento no se toca: se informa como falla transitoria con el
   mensaje de Factus.
10. **Tiquete:** después de confirmar la transacción de la venta, la caja espera ⚙️ hasta 3 s (`billing.ticket_wait_seconds`) los datos
    fiscales; si llegan imprime "Factura electrónica de venta" con número, CUFE y QR; si no, la leyenda "en proceso" con el número interno.
    La espera nunca convierte la venta en un error.

## Consecuencias
- ✅ La caja nunca se detiene por Internet, Factus o la DIAN; no hay duplicados aunque se reintente o se caiga el proceso.
- ✅ Una hora pico no provoca bloqueos del proveedor (ritmo propio + pausa global por 429).
- ⚠️ Documentos pendientes mientras no haya Internet: el cliente recibe la factura después (correo o reimpresión).
- ⚠️ **Limitación:** un documento rechazado que se **cancela en el POS** (p. ej. se anuló la venta) sigue en Factus y **bloquea los envíos
  hasta que el dueño lo borre en el panel de Factus**; el POS no lo elimina porque ya no lo va a reenviar.
- ⚠️ Una sola cola por servidor de tienda (una pasada a la vez); la emisión desde la nube queda para después de la sincronización.
