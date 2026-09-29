# Fase 5 · Terceros, proveedores y compras — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-28
> Documentos: [propuesta aprobada](fase-05-propuesta.md) · ADR [0023](../adr/0023-terceros-unicos-con-roles.md) ·
> [0024](../adr/0024-costo-neto-de-compra.md) · [0025](../adr/0025-lotes-solo-con-cantidades-y-fefo.md) ·
> [0026](../adr/0026-cartera-por-pagar-como-libro.md)

## 1. Resultado frente a los criterios de aceptación (§14)

| Criterio | Resultado | Evidencia |
|---|---|---|
| Terceros con NIT y DV validados; identificación única; Consumidor final sembrado | ✅ | `Terceros_con_DV_identificacion_unica_busqueda_y_consumidor_final`: DV incorrecto → 400, duplicado → 409, cédula como persona jurídica → 400; búsqueda sin tildes y por NIT con o sin DV; el Consumidor final no se modifica |
| Proveedores con productos, código del proveedor y último costo; proveedor que no factura marcado para documento soporte | ✅ | La compra de un proveedor con `issuesInvoices=false` queda `requiresSupportDocument` y se lista con `?requiresSupportDocument=true`; el último costo se actualiza al contabilizar |
| Orden de compra completa, recepción parcial y total con tolerancia | ✅ | `Semana_de_compras…`: orden → recepción parcial (`PARTIALLY_RECEIVED`) → exceso rechazado (`RECEIPT_EXCEEDS_ORDER`, tolerancia 0 %) → recepción final (`RECEIVED`); una compra sin líneas contra la orden propone lo pendiente |
| Compra con descuentos, fletes prorrateados, IVA descontable o no, retenciones; totales cuadran; kardex al costo neto | ✅ | Ejemplo de la propuesta: 10 cajas × 24 a $60.000, descuento $30.000, flete $12.000 → **$2.425** por unidad (unitaria y en vivo); retefuente 2,5 % reduce lo que se paga, no el costo; sin total de factura o si no cuadra, no se contabiliza |
| Lotes y vencimientos en la entrada, FEFO en las salidas, alertas de vencimiento; cantidades por lote cuadran | ✅ | `Lotes_obligatorios_FEFO…`: sin lote no se contabiliza; una avería de 18 sale 15 del lote que vence primero y 3 del siguiente; un lote no queda negativo; lotes por vencer con `inventory.expiry_alert_days`; la verificación compara cada lote con sus movimientos |
| Cartera por pagar como libro, pagos a varias facturas, edades; anulación de pagos por asiento inverso | ✅ | Un pago cubre dos facturas; el libro muestra `CHARGE → PAYMENT → RETURN`; sobrepago → `OVERPAYMENT`; cartera por edades y estado de cuenta; anular el pago reabre la cuenta |
| Devolución al costo de compra y su liquidación; anulación de compra con sus bloqueos | ✅ | Devolución al costo neto y del lote original; nota crédito, reintegro (la cuenta termina en 0) y reposición; anular: revierte kardex (`REVERSAL`) y cartera; bloqueada con pagos, con devoluciones o si deja existencias negativas |
| Permisos en todos los endpoints; auditoría; cambios por campo de los maestros nuevos | ✅ | Cajero ve terceros pero no compras; Compras registra pero no contabiliza; Contador consulta la cartera pero no paga; eventos `sync.entity_changed.v1` de `parties.parties`; auditoría de contabilizar, anular, pagar y devolver |
| Dos usuarios contabilizan la misma compra → uno solo lo logra | ✅ | `Dos_usuarios_contabilizan_la_misma_compra_y_solo_uno_lo_logra` |
| `build.ps1` en verde; cobertura de los dominios nuevos ≥ 90 % | ✅ | Ver §3 |
| Docs 04, 05, 07 y 08, ADRs e informe | ✅ | ADR-0023 a 0026; notas de implementación en 04, 05, 07 y 08; `http/fase-05.http` |

## 2. Qué se construyó

```
src/Server/Pos.Server.Migrations   V010 terceros · V011 medios de pago (esquema cash) · V012 compras (14 tablas) · V013 lotes
                                   tipos de documento PURCHASE_ORDER y PAYABLE_PAYMENT · 13 permisos · libro de cartera de solo inserción
src/BuildingBlocks
  Pos.SharedKernel                 Fiscal/Nit (DV del NIT, antes en Organization) para empresa y terceros
src/Modules/Parties   (nuevo)      Terceros y contactos; búsqueda; Consumidor final; IPartyDirectory para compras y ventas
src/Modules/Cash      (nuevo)      Medios de pago con código DIAN (crece en la Fase 6); IPaymentMethodDirectory
src/Modules/Purchasing (nuevo)     Proveedores y sus productos, órdenes, compras con costo neto, cartera como libro, pagos,
                                   devoluciones, cartera por edades y estado de cuenta
src/Modules/Inventory              Lotes (IInventoryLots), FEFO, salida valorizada, reversión (ReverseAsync), ajustes por lote,
                                   /inventory/lots, verificación de lotes
src/Modules/Catalog                ICatalogReader: presentaciones, impuestos con tarifa vigente, precio de venta sin impuestos
src/Modules/Organization           Series de numeración de los tipos de documento nuevos al arrancar (instalaciones existentes)
src/Server/Pos.Server.Host         El arranque ya no se queda esperando si un inicializador falla: lo registra y reintenta
http/fase-05.http
```

Datos iniciales de cada empresa (también en las instalaciones existentes, al arrancar): Consumidor final, 7 medios de pago
(Efectivo, Tarjeta débito, Tarjeta crédito, Transferencia, Nequi, Daviplata, Bono) y las series de numeración de órdenes de
compra y pagos a proveedor.

## 3. Pruebas y cobertura

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.ArchitectureTests | 21 | R1–R8 con los 15 ensamblados nuevos (solo Inventory escribe el kardex; compras usa `IInventoryPosting`) |
| Pos.Database.Tests | 56 | + identificación única salvo fusionados, NIT con DV, factura única salvo anuladas, libro de cartera de solo inserción y con signo por tipo, lotes por sucursal, filas de lote sin valor, una sola reversión, solo el efectivo afecta el cajón |
| Pos.Infrastructure.UnitTests | 48 | Sin cambios |
| Pos.Modules.Parties.UnitTests *(nuevo)* | 6 | Persona natural y jurídica, DV, tipos de documento, contactos, Consumidor final, fusión |
| Pos.Modules.Cash.UnitTests *(nuevo)* | 3 | Medios de pago: solo el efectivo afecta el cajón; el efectivo no se inactiva |
| Pos.Modules.Purchasing.UnitTests *(nuevo)* | 26 | Costo neto ($2.425), IVA descontable o no, prorrateo exacto, compras, órdenes, libro de cartera, pagos, edades, devoluciones |
| Pos.Modules.Inventory.UnitTests | 29 | + salida valorizada (devolución al costo de compra), reversión |
| Pos.Modules.Catalog / Identity / Organization.UnitTests | 40 / 33 / 19 | Las pruebas del DV se movieron de Organization a SharedKernel |
| Pos.Server.IntegrationTests | 97 | + 6 escenarios de la Fase 5 (terceros, semana de compras, anulación, lotes y FEFO, concurrencia, permisos) |
| Pos.SharedKernel.UnitTests | 82 | + DV del NIT |
| **Total** | **460** | Fase 4: 411 |

Cobertura de líneas (combinada entre proyectos):

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.Modules.Parties.Domain | 100 % | 90 % ✅ |
| Pos.Modules.Cash.Domain | 100 % | 90 % ✅ |
| Pos.Modules.Purchasing.Domain | 99,8 % | 90 % ✅ |
| Pos.Modules.Inventory.Domain / Catalog.Domain | 99,7 % / 99,2 % | 90 % ✅ |
| Pos.Infrastructure / Pos.Server.Migrations / Pos.SharedKernel | 94,7 % / 93,7 % / 99,6 % | 85 / 85 / 95 % ✅ |
| Purchasing.Api / Purchasing.Infrastructure | 99,3 % / 98,2 % | — |
| Parties.Infrastructure / Cash.Infrastructure | 98,9 % / 100 % | — |
| Purchasing.Application / Parties.Application / Cash.Application | 77 % / 81,4 % / 93,9 % | — (ramas de error poco frecuentes) |

## 4. Desviaciones respecto de la propuesta (y por qué)

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Permisos §7 | + `purchasing.purchase.view` | Consultar proveedores, órdenes, compras y devoluciones sin poder registrarlas (Contador, Inventario) |
| 2 | Lotes únicos por producto | Únicos por **sucursal** y producto (`inventory_lots.branch_id`) | Dos tiendas que reciben el mismo lote del fabricante sin conexión no chocan al sincronizar |
| 3 | Devolución al costo de la compra | Además `credit_total`: la cartera baja en la proporción del total de la línea **con impuestos** | El kardex sale al costo (sin IVA descontable) pero la nota crédito del proveedor incluye el IVA |
| 4 | Liquidación por reposición | La mercancía vuelve a entrar al costo de la compra y en el lote original; la deuda se restablece | Reposición = cambio de mercancía; si trae otro lote, se registra con un ajuste por lote |
| 5 | Ajuste por lote | Una línea por producto en cada ajuste (con lote opcional) | La restricción única de la Fase 4 (ajuste, producto) se conserva; varios lotes = varios ajustes |
| 6 | Compra de contado | Nace pagada con un pago automático; con Efectivo es "efectivo fuera de caja" hasta la Fase 6 | La caja (jornada y movimiento) llega en la Fase 6 (RN-PUR-03) |
| 7 | — | Los cargos no forman parte de la base de los impuestos | Simplificación: el flete facturado aparte por el transportista no lleva el IVA del producto |
| 8 | — | Corrección del arranque (`DatabaseStartup`) | Si un inicializador fallaba con un error que no era de la BD, el servidor esperaba para siempre sin abrir el puerto |

## 5. Limitaciones conocidas y pendiente

1. **Fusión de terceros duplicados**: modelada (`MERGED`, `merged_into_id`); la ejecuta la sincronización.
2. **Retenciones automáticas**: digitadas por compra (pregunta 1); el cálculo con bases en UVT se define con el contador.
3. **Documento soporte y XML del proveedor**: marcados; se emiten/cargan en la Fase 11-B con Factus.
4. **Pago y compra de contado desde la caja**: Fase 6 (`payable_payments.cash_session_id` reservado).
5. **Venta de lotes vencidos**: se bloquea en la caja (Fase 7); hoy el FEFO también saca primero lo vencido.
6. Pendientes externos: validación del Servicio de Windows de la Fase 1 y consulta a Factus antes de la Fase 7.

## 6. Cómo validarlo

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1          # compila, pruebas y cobertura → BUILD OK
powershell -ExecutionPolicy Bypass -File .\tools\scripts\dev-db.ps1   # aplica las migraciones 010–013
dotnet run --project src\Server\Pos.Server.Host              # http://localhost:5480
```

Luego ejecuta `http/fase-05.http` en orden.
