# Fase 11-B · Facturación electrónica con Factus — Informe

- **Estado:** Implementada (apagada) — pendiente de la prueba en el sandbox de Factus · 2026-09-30
- **Propuesta:** [fase-11b-propuesta.md](fase-11b-propuesta.md) (aprobada con todas las recomendaciones de la §14)
- **Decisiones:** [ADR-0059](../adr/0059-modo-de-emision-y-factura-electronica-por-venta-con-factus.md) (modo de emisión y factura por venta) ·
  [ADR-0060](../adr/0060-emision-asincrona-idempotente-y-contingencia.md) (emisión asíncrona, idempotencia y contingencia) ·
  [ADR-0061](../adr/0061-mapeo-fiscal-desde-la-venta-guardada.md) (mapeo fiscal)
- **Guía para el dueño:** [guia-factus.md](../guia-factus.md) · **API:** `http/fase-11b.http` · **Rama:** `fase-11b-factus`

## 1. Criterios de aceptación (§13 de la propuesta)

| Criterio | Estado | Evidencia |
|---|---|---|
| Modo `OFF` / `ON_REQUEST` / `EVERY_SALE`; en `OFF` todo sigue como hoy | ✅ | `ElectronicBillingTests.En_modo_OFF_todo_sigue_como_hoy…`, `FiscalTicketTests.En_modo_OFF_el_tiquete_queda_igual_que_hoy` |
| Factura y nota crédito **aceptadas en el sandbox de Factus** desde ventas, anulaciones, cambios y garantías | ⏳ **Pendiente de la prueba conjunta** | Aceptadas contra el **Factus simulado** (`FactusEndToEndTests.Venta_factura_aceptada_anulacion_cambio_y_garantia…`); falta el sandbox real con tus credenciales |
| Sin Internet la venta se completa y el documento se envía al volver, sin duplicados | ✅ (simulado) | `Sin_Internet_la_venta_sigue…`, `Sin_Internet_queda_en_contingencia_y_se_envia_al_volver_sin_duplicados` |
| Rangos sincronizados, asignados por sucursal/caja y con alertas | ✅ (simulado) | `Rangos_sincronizados_desde_Factus_y_el_429_se_respeta`, `Sin_rango_vigente…`, pruebas unitarias de `FiscalNumberingRange` |
| Tiquete con número, CUFE y QR cuando llegan a tiempo; reimpresión con los datos | ✅ | `FiscalTicketTests` (5) |
| Rechazos visibles, corregibles y auditados; reporte de conciliación | ✅ | `Un_rechazo_queda_REJECTED…`, `Rechazo_DIAN_…se_borra_en_Factus_y_se_reenvia`, `FiscalReconciliationReportTests` |
| `dotnet build` sin advertencias; doc 08; guía de activación; ADRs e informe | ✅ | `build.ps1` en verde (1.240 pruebas, 0 fallos; cobertura de Billing.Domain 100 %) · doc 08 (notas de la Fase 11-B), guía Factus, ADR-0059 a 0061, este informe |

## 2. Qué se construyó

| Bloque | Entregado |
|---|---|
| 11B.1 Adaptador y credenciales | `FactusApiClient` (Factus API v2: OAuth2 con token en memoria renovado, reintento único ante 401, traducción de 201/200/409/422/429/5xx), `FactusFiscalProvider` (nunca lanza; resultado neutro), `FactusTokenManager`, `FactusResponseReader`. Credenciales cifradas con **DPAPI** en `billing.provider_settings` (nunca se devuelven ni se registran); ambiente `SANDBOX`/`PRODUCTION`; adaptador por `Pos:Billing:Provider` (`NONE` por defecto, `FACTUS`, `FAKE`) |
| 11B.2 Rangos y mapeo fiscal | `billing.fiscal_numbering_ranges` sincronizados desde Factus (manual y cada 24 h), asignación por sucursal/caja y tipo, alertas al 90 % y a 30 días. **Modelo fiscal neutro** (`FiscalModel.cs`) armado desde el documento guardado (`FiscalDraftBuilder`, concilia contra lo cobrado) y `FactusDraftMapper` (consumidor final, bolsa como ítem, base exacta, pesables con 4 estrategias, `cash_rounding_amount`, medios de pago DIAN) |
| 11B.3 Emisión, contingencia y tiquete | Documento `PENDING` en la transacción de la venta + outbox LOCAL; `FiscalQueueRunner`/`FiscalQueueWorker` (orden de llegada, reclamo con consulta de estado, contingencia, espera creciente, 60/min, pausa global por 429). `FiscalTicketWait` (Sales): espera ⚙️ 3 s **después** de confirmar; tiquete "Factura electrónica de venta" con número, CUFE y QR o leyenda "en proceso" |
| 11B.4 Notas crédito y documento soporte | Nota crédito por anulación (total), cambio y garantía (parcial), esperando la factura aceptada; factura no aceptada → `CANCELLED`. **Documento soporte** al contabilizar compras a proveedores que no facturan y **nota de ajuste** al anular una compra con el soporte aceptado (Purchasing) |
| 11B.5 Conciliación, alertas y pruebas | `GET /billing/reconciliation`, reporte `FISCAL_RECONCILIATION` (catálogo de la Fase 9), `GET /billing/alerts`, reintento, corrección del comprador (`buyer_fiscal`), `POST /billing/queue/process`. Auditoría: rechazo, rango faltante, alerta de rango, corrección, cambios de modo/credenciales/rangos |
| Interfaz | **Administración → Facturación** (`/admin/facturacion`): Configuración (modo con confirmación de cotización y contador, ambiente, credenciales de solo escritura), Rangos (sincronizar, asignar), Documentos (filtros, detalle con eventos, reintentar, corregir comprador), Alertas y conciliación |
| BD | Migración `V2026.10.032__billing__electronic.sql`; permiso `billing.settings.manage` en `R__identity__permissions_catalog.sql` |
| Pruebas de apoyo | `tests/Pos.Modules.Billing.FactusFake`: **servidor Factus simulado** (OAuth2, facturas, notas, documento soporte, notas de ajuste, rangos, idempotencia, 409, rechazos que bloquean, borrado, 429, caídas) |

## 3. Pruebas

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| `Pos.Modules.Billing.Factus.Tests` | 55 | Cliente HTTP (token, 401, 409, 422, 429, red), mapeo (IVA, exento, excluido, INC, bolsa, descuentos, redondeo del efectivo, varios medios, crédito, pesables en sus 4 estrategias, consumidor final, proveedor con cédula), serialización JSON y `FactusFiscalProvider` (duplicado, rechazo que bloquea → borrar y reenviar) |
| `Pos.Modules.Billing.UnitTests` | 50 | Dominio: estados del documento, reclamo, cancelación, reintento, corrección, rangos (selección, uso, alertas), modos, borradores y conciliación de totales |
| `Pos.Server.IntegrationTests/Phase11B` | 15 + 5 del tiquete + 1 de conciliación | Emisión con el proveedor simulado (10) y de punta a punta contra el Factus simulado (5); tiquete (5); reporte de conciliación (1) |
| Totales y `build.ps1` | 1.240 | `build.ps1` en verde, 0 fallos (Pos.Server.IntegrationTests 150; Billing.Factus.Tests 55; Billing.UnitTests 50) |

## 4. Decisiones y desviaciones respecto de la propuesta

- **Migración V2026.10.032** en lugar de la V030 prevista (la 030 y la 031 ya existían).
- **V032 editada en su sitio durante la fase** (se agregaron la nota de ajuste, el estado `CANCELLED` y datos del adquirente corregido): una
  **BD local que ya la aplicó debe recrearse** (`dev-db.ps1`); el migrador rechaza un script versionado cambiado.
- **Nota de ajuste para anular compras** (`ADJUSTMENT_NOTE`, origen `PURCHASE_VOID`): la propuesta solo mencionaba el documento soporte;
  anular una compra con el soporte ya aceptado exige su nota de ajuste ante la DIAN.
- **Pausa global por 429:** cuando Factus pide esperar se detiene toda la cola, no solo el documento (así no se gasta el límite en hora pico).
- **Espera del tiquete después de confirmar la venta** (`FiscalTicketWait` en la capa API de Sales), no dentro de la transacción: la cola
  solo ve lo confirmado y la espera nunca convierte la venta en un error.
- **Leyenda "Factura electrónica de venta"** en el tiquete con datos fiscales (y "Nota crédito electrónica" en anulaciones y cambios);
  el comprobante interno sigue diciendo "No es factura".
- **CASH_SUPERVISOR con `billing.document.view` y `billing.document.manage`** (atiende rechazos y reintentos en la tienda); la configuración
  (`billing.settings.manage`) es solo de OWNER y ADMIN.
- Rechazo que bloquea en Factus: en el **reenvío** el adaptador borra el documento no validado con el mismo `reference_code` y reenvía;
  el bloqueo por otro documento se informa como falla transitoria (ver limitación).

## 5. Limitaciones y pendientes

- ⏳ **Prueba conjunta en el sandbox real de Factus** con tus credenciales (entregable del plan): recorrer la lista de la
  [guía §7](../guia-factus.md) y confirmar los **supuestos** de la [guía §9](../guia-factus.md) (consumidor final, KGM/GRM, nota "por
  valor", `current` del rango, duplicado 200, cédula como NIT, "No informada", impuestos sumados a la base, crédito a 30 días, fecha en
  contingencia, 201 vs. 422 en rechazos).
- ⏳ **Costo por volumen:** cotización de Factus antes de encender `EVERY_SALE` (~60.000 documentos al mes para 2.000 ventas diarias).
- ⏳ **Validación del contador:** modo por venta a consumidor final, impuestos, pesables "por valor", contingencia y documento soporte.
- ⚠️ Un documento **rechazado que se cancela en el POS** sigue bloqueando los envíos en Factus **hasta borrarlo en su panel**.
- ⚠️ En contingencia la fecha de la factura es la del envío (Factus no recibe fecha de emisión): a validar con el contador.
- Fuera de alcance (propuesta §1): DEE POS con otro proveedor, RADIAN y recepción de facturas de proveedores, nómina electrónica,
  facturación desde la nube, personalización del PDF.

## 6. Cómo probarlo

1. Recrea la BD local (`dev-db.ps1`) y arranca el servidor con `Pos__Billing__Provider=FAKE` (proveedor simulado) o `FACTUS` (sandbox).
2. **Administración → Facturación → Configuración:** credenciales (con FAKE sirve cualquier valor), ambiente *Pruebas*, modo *Factura
   electrónica en cada venta*.
3. **Rangos:** Sincronizar y asignar el de factura y el de nota crédito a la sucursal.
4. Vende en la caja: el tiquete debe traer "Factura electrónica de venta" con número, CUFE y QR. Anula una venta y haz un cambio: notas
   crédito. Revisa **Documentos** y **Alertas y conciliación**.
5. Con Factus real: sigue la [guía de activación](../guia-factus.md) §3 a §7 y `http/fase-11b.http`. Anota toda diferencia de valores.
