# Fase 9 · Reportes — Informe de implementación

- **Estado:** Implementada — pendiente de tu validación · 2026-09-29
- **Propuesta:** [fase-09-propuesta.md](fase-09-propuesta.md) (aprobada con las seis recomendaciones de la §14)
- **ADR:** [0044](../adr/0044-reportes-de-solo-lectura-sobre-vistas.md) · [0045](../adr/0045-catalogo-de-reportes-y-exportacion.md) ·
  [0046](../adr/0046-ventas-netas-y-fecha-de-negocio-en-reportes.md)
- **Pruebas manuales:** [http/fase-09.http](../../http/fase-09.http)

## 1. Qué se entregó (bloques 9.1 a 9.5)

| Bloque | Entregado |
|---|---|
| 9.1 Motor, catálogo y exportación | Módulo `Reporting` (Contracts, Application, Infrastructure, Api; sin dominio). Catálogo de **33 reportes** en el código; `GET /api/v1/reports` (catálogo según permisos), `GET /api/v1/reports/{code}` (JSON paginado con totales de todas las filas) y `&format=xlsx\|csv\|pdf` (archivo, **auditado**). Consultas en transacción de solo lectura con tiempo límite. Exportadores: MiniExcel, CSV nativo y PDFsharp-MigraDoc |
| 9.2 Ventas, utilidad e impuestos | `SALES_DAILY`, `SALES_BY_HOUR`, `SALES_BY_PAYMENT`, `SALES_BY_CASHIER`, `SALES_BY_TERMINAL`, `SALES_BY_PRODUCT`, `SALES_BY_CATEGORY`, `SALES_BY_BRAND`, `SALES_BY_CUSTOMER`, `SALES_BY_PRICE_LIST`, `DISCOUNTS_AND_PROMOTIONS`, `PROFIT_BY_DAY/PRODUCT/CATEGORY/CASHIER`, `TAXES_SALES`, `TAXES_PURCHASES`, `SALES_BOOK` |
| 9.3 Inventario, compras y gastos | `INVENTORY_VALUATION` (hoy o `asOf`), `BELOW_MINIMUM` (sugerido y proveedor preferido), `NO_ROTATION`, `EXPIRING_LOTS`, `ADJUSTMENTS`, `PURCHASES_BY_SUPPLIER`, `SUPPLIER_RETURNS`, `PAYABLES_AGING`, `EXPENSES_BY_CATEGORY` |
| 9.4 Caja y antifraude | `CASH_CLOSINGS` (por medio), `CASH_WITHDRAWALS`, `ANTIFRAUD_BY_CASHIER` (umbral ⚙️), `ANTIFRAUD_EVENTS` |
| 9.5 Tablero del día | `GET /api/v1/reports/dashboard`: hoy vs. mismo día de la semana anterior (tiquetes, venta neta, ticket promedio, anuladas), utilidad del día (solo con permiso), ventas por hora, top 10, cajas abiertas, cierres con diferencia sin revisar, productos bajo mínimo, lotes por vencer, solicitudes de titulares por vencer |

### Base de datos
- `V2026.10.027__reporting__schema.sql`: esquema `reporting` e índices por fecha de negocio (ventas, líneas, pagos, devoluciones,
  kardex, movimientos de caja, compras y gastos).
- `R__reporting__views.sql` (repetible): **31 vistas** (lista en ADR-0044), incluida `antifraud_events`.
- `A__system__privileges.sql`: `pos_app` y `pos_backup` usan el esquema; `pos_app` solo tiene `SELECT` sobre las vistas.
- `R__identity__permissions_catalog.sql`: 9 permisos nuevos (85 en total).

### Permisos y roles (§8)
| Permiso | Roles de sistema |
|---|---|
| `reporting.sales.basic` | CASH_SUPERVISOR, ACCOUNTANT, PROMOTIONS_MANAGER, OWNER, ADMIN |
| `reporting.sales.advanced` | ACCOUNTANT, PROMOTIONS_MANAGER, OWNER, ADMIN |
| `reporting.profit.view` (sensible) | ACCOUNTANT, OWNER, ADMIN |
| `reporting.taxes.view` | ACCOUNTANT, OWNER, ADMIN |
| `reporting.inventory.view` | INVENTORY, PURCHASING, ACCOUNTANT, OWNER, ADMIN |
| `reporting.purchases.view` | PURCHASING, ACCOUNTANT, OWNER, ADMIN |
| `reporting.cash.view` | CASH_SUPERVISOR, ACCOUNTANT, OWNER, ADMIN |
| `reporting.antifraud.view` (sensible) | CASH_SUPERVISOR, OWNER, ADMIN |
| `reporting.report.export` (sensible) | ACCOUNTANT, OWNER, ADMIN |

Las columnas de costo (costo, utilidad, margen, valor del inventario, último costo) se ocultan a quien no tenga
`inventory.cost.view` ni `reporting.profit.view`.

### Configuración nueva (⚙️, alcance empresa)
| Clave | Valor por defecto |
|---|---|
| `reporting.max_range_days` | 366 |
| `reporting.max_rows` | 100.000 |
| `reporting.statement_timeout_seconds` | 30 |
| `reporting.antifraud_threshold_factor` | 2 (se resalta al cajero con más del doble de eventos por tiquete que el promedio) |
| `reporting.dashboard_expiring_days` | 15 |

## 2. Desviaciones respecto a la propuesta

| Propuesta | Implementado | Motivo |
|---|---|---|
| Rol de BD `pos_report` con `statement_timeout` | Rol de la aplicación + `SET TRANSACTION READ ONLY` + `SET LOCAL statement_timeout` en conexión propia; `pos_app` solo `SELECT` en `reporting` | Mismo efecto (la BD rechaza escrituras y corta el reporte lento) sin administrar otra contraseña de BD por instalación (ADR-0044) |
| Permiso `reporting.export` | `reporting.report.export` | El formato de permisos (código y BD) exige `modulo.recurso.accion` |
| "Pedido pendiente" del sugerido | Órdenes de compra APROBADAS, ENVIADAS o RECIBIDAS PARCIALMENTE (cantidad base pendiente) | Precisión de la regla |
| Solicitudes de titulares por vencer en el tablero | Las abiertas que vencen en ≤ 5 días calendario | Sencillez; el módulo Customers alerta por días hábiles |

## 3. Validación realizada (según tu forma de trabajo: construir y verificar coherencia)

| Verificación | Resultado |
|---|---|
| `dotnet build Pos.slnx -c Release` | **0 advertencias, 0 errores** |
| `ReportingSchemaTests` (BD real PostgreSQL 18): las 31 vistas compilan y se consultan con `pos_app`; `pos_app` no puede escribirlas | ✔ |
| `ReportsApiTests` (una prueba corta con dos ventas del escenario de la Fase 7): **los 33 reportes se ejecutan**; cajera sin reportes (catálogo vacío y 403); exportación CSV, Excel y PDF; reporte inexistente 404; rango > 366 días 400; tablero | ✔ |
| Igualdades de cuadre (§0) en esa prueba: Σ por medio = total vendido; Σ por producto = ventas netas; Σ por cajero = total; Σ impuestos por tarifa = impuestos del día; libro de ventas = total; utilidad = venta neta − costo (por día y por producto); valorizado hoy = valorizado `asOf` hoy | ✔ |
| `ConformityTests` (catálogo de permisos del código = BD; todo endpoint declara seguridad; modelo EF = BD) | ✔ 5/5 |
| `ArchitectureTests` (reglas R1–R8 con el módulo nuevo) | ✔ 21/21 |

No se ejecutó `build.ps1` completo ni la batería completa de pruebas (por tu indicación).

## 4. Qué NO se probó — y qué debes probar tú

1. **Números con datos reales** de varios días: cambios y garantías (ventas netas y utilidad netas de devoluciones), descuentos
   globales, promociones, anulaciones y ventas en la madrugada (fecha de negocio vs. hora).
2. **Libro de ventas diario y reporte de impuestos**: revisarlos con tu **contador** (formato, excluidos/exentos, impuestos al consumo
   y bolsas). Las tarifas 5 % y 19 % tienen columnas propias; otras tarifas de IVA van en "Otro IVA".
3. **Inventario a una fecha** contra el kardex de un producto (`GET /inventory/kardex`) en una fecha pasada, y con productos con lotes.
4. **Bajo mínimo** con políticas de mínimo/máximo y órdenes de compra pendientes.
5. **Antifraude**: generar líneas eliminadas, cancelaciones, anulaciones, descuentos, precios cambiados, cajón sin venta, lotes vencidos,
   ajustes rápidos y un cierre con diferencia; verificar conteos, valores y el resaltado con el umbral.
6. **Archivos**: abrir el Excel y el CSV en tu Excel (coma decimal, tildes), y el PDF (encabezado, totales, páginas, reportes anchos
   como el libro de ventas y el antifraude, que usan letra pequeña en horizontal).
7. **Permisos por rol**: contador (ve utilidad, exporta), supervisor (ventas básicas, caja y antifraude, sin costos), inventario y
   compras (sin costos en reportes de ventas); costos ocultos sin permiso; exportación registrada en la auditoría.
8. **Rendimiento** con volumen (un mes de ventas reales) y el corte por tiempo (`REPORTING.TIMEOUT`).
9. **Detalle por cliente**: un cliente sin autorización SERVICE (o que la revocó) no debe aparecer por nombre.
10. PDF en Linux (la nube): requiere fuentes DejaVu o Liberation instaladas (no aplica a la tienda en Windows).

## 5. Archivos principales

```
src/Modules/Reporting/Pos.Modules.Reporting.Contracts/ReportingContracts.cs   permisos, DTOs, formatos
src/Modules/Reporting/Pos.Modules.Reporting.Application/ReportModel.cs       definición de reporte, parámetros, columnas
src/Modules/Reporting/Pos.Modules.Reporting.Application/ReportCatalog.cs     los 33 reportes y su SQL
src/Modules/Reporting/Pos.Modules.Reporting.Application/ReportUseCases.cs    motor, configuración, casos de uso (catálogo, JSON, exportación, tablero)
src/Modules/Reporting/Pos.Modules.Reporting.Infrastructure/ReportingInfrastructure.cs   solo lectura con tiempo límite, tablero
src/Modules/Reporting/Pos.Modules.Reporting.Infrastructure/ReportExporter.cs          Excel, CSV y PDF
src/Modules/Reporting/Pos.Modules.Reporting.Api/ReportingModule.cs          endpoints
src/Server/Pos.Server.Migrations/Scripts/2026.10/V2026.10.027__reporting__schema.sql
src/Server/Pos.Server.Migrations/Scripts/repeatable/R__reporting__views.sql
tests/Pos.Database.Tests/ReportingSchemaTests.cs · tests/Pos.Server.IntegrationTests/Phase9/ReportsApiTests.cs
```

Documentos actualizados: 02 (módulo Reporting implementado, sin reportes por plan), 06 (permisos de reportes), 07 (inventario a una
fecha y pedido pendiente), `licencias-terceros.md` (PDFsharp-MigraDoc) y el índice de ADR.
