# ADR-0013 · Numeración interna separada de la numeración fiscal

- **Estado:** Aceptada · 2026-09-28 · Fase 2

## Contexto
La DIAN controla el consecutivo fiscal (resolución, prefijo corto, rango). El proveedor elegido, **Factus**, asigna el número fiscal y el CUFE en línea. Mezclar el número interno con el fiscal habría limitado la facturación electrónica y la venta sin conexión. Detalle: [revisión §3 y §10.3](../fases/fase-02-revision-arquitectonica.md).

## Decisión
- **UUID v7**: identidad técnica. **Número interno** por serie (`system.document_series`): prefijo **generado** `{sucursal}{caja}` o `{sucursal}` (no editable, no es el prefijo DIAN), asignado con `UPDATE … RETURNING` dentro de la transacción del documento: sin duplicados y sin huecos en operación normal. Series de venta **por caja** (sin contención; la caja autónoma numera sola).
- **Número fiscal**: en `billing.fiscal_documents` (Fase 11-B), lo asigna el proveedor. La venta nunca espera al número fiscal.
- El "consecutivo comercial" impreso no es un contador: es el número fiscal si existe; si no, el interno.
- Huecos inevitables (restauración, caja perdida) se registran como **huecos justificados** (`NUMBERING_GAP`). Reutilizar el código de una caja continúa su serie.

## Consecuencias
- ✅ La numeración interna nunca limita la facturación electrónica ni la operación offline.
- ⚠️ RN-GEN-06 se redacta de nuevo: obligatorio el consecutivo fiscal; el interno es único y sin huecos en operación normal.
