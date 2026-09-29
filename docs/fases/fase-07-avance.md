# Fase 7 · POS y ventas — Nota de relevo (trabajo en progreso)

> Fecha: 2026-09-29 · Rama: **`fase-7-ventas`** (no está en `main`) · Propuesta aprobada: [fase-07-propuesta.md](fase-07-propuesta.md)
> Esta nota es para retomar el trabajo desde otro equipo. Cuando la fase termine se reemplaza por `fase-07-informe.md`.

## 1. Cómo retomar en el otro equipo

```powershell
git clone https://github.com/JaraAlvarez/sistemaPost.git   # o git fetch si ya está clonado
cd sistemaPost
git switch fase-7-ventas
dotnet build Pos.slnx -c Release                             # debe compilar sin advertencias
# Pruebas de la Fase 7 (requieren Docker Desktop encendido: usan Testcontainers)
dotnet test --project tests/Pos.Server.IntegrationTests -c Release -- --filter-class "*Phase7.*"
dotnet test --project tests/Pos.Database.Tests -c Release
```

Requisitos del equipo: SDK de .NET 10 (versión en `global.json`), Docker Desktop, PowerShell. Para probar a mano:
`powershell -File tools/scripts/dev-db.ps1` (aplica las migraciones 016–019) y `dotnet run --project src/Server/Pos.Server.Host`.

Estado al dejarlo: **la solución compila**, las **58 pruebas de BD** y las **5 pruebas de integración de la Fase 7** pasan, y las pruebas de
conformidad del modelo EF contra la BD pasan. **No** se corrió todavía `build.ps1` completo (todas las pruebas + cobertura).

## 2. Qué está hecho

| Bloque | Hecho |
|---|---|
| 7.1 Ventas | Módulo `Sales` (5 proyectos): venta persistida (`OPEN`/`ON_HOLD`/`COMPLETED`/`CANCELLED`/`VOIDED`), motor puro `SaleCalculator` (precio con impuestos incluidos, promociones, descuento de línea y global prorrateado, impuestos porcentuales y fijos), `PaymentAllocator` (medios sin cambio primero, efectivo redondeado a $50 con cambio), cobro atómico e idempotente (`Idempotency-Key`), número por caja, kardex FEFO sin saldo negativo con costo por línea, movimientos de caja por medio, evento `sales.sale_completed.v1` |
| Existencias | Aviso al escanear y verificación al cobrar (`SALES.INSUFFICIENT_STOCK`); **ajuste rápido autorizado** `POST /inventory/quick-adjustments` (permiso `inventory.adjustment.quick`); lote vencido exige autorización (`POST /sales/{id}/lines/expired`, `sales.expired.sell`) |
| Descuentos | Siempre con autorización (`sales.discount.apply`, el cajero no lo tiene) |
| 7.2 Anulación y cambios | Anulación solo con la jornada de la venta abierta; cambio de mercancía por igual o mayor valor (`POST /exchanges`, medio del sistema `CAMBIO` que no entra al cajón); reintegro por garantía solo del propietario (`POST /exchanges/warranty-refund`) |
| Billing | Módulo `Billing`: comprobante interno `INTERNAL_RECEIPT`/`NOT_REQUIRED` por venta y cambio; anulación; `IFiscalProvider` con proveedor nulo; trabajador del outbox listo para Factus (11-B); setting `billing.electronic_enabled` = false |
| 7.4 Promociones | Módulo `Promotions`: tipos `MULTI_BUY` (lleve N pague M), `SPECIAL_PRICE`, `PERCENT_OFF`, `QUANTITY_PRICE`, `COMBO`; vigencia, días, horario, sucursales; borrador → activa → pausada → terminada; simulador; reporte; rol nuevo `PROMOTIONS_MANAGER` |
| Caja | `IOpenSalesProbe` (no se cierra con ventas pendientes, `CASH.OPEN_SALES`), `ICashRegister.RecordSaleMovementsAsync` y `GetOpenSessionAsync` |
| 7.3 Impresión (parcial) | Librería `src/BuildingBlocks/Pos.Printing`: tiquete neutro (JSON polimórfico), diseño 42/32 columnas, generador ESC/POS (PC850, negrita, doble tamaño, CODE128, QR, corte, pulso del cajón). Impresora por caja: `GET/PUT /organization/terminals/{id}/receipt-printer` (tabla `org.terminal_devices`). El tiquete sale en la respuesta de cobrar/anular/reimprimir |
| Migraciones | `V016 promotions`, `V017 sales` (incluye `EXCHANGE_CREDIT` en medios de pago), `V018 billing`, `V019 org.terminal_devices`; permisos (68 en total), tipo de documento `PROMOTION`, privilegios de los esquemas nuevos |
| Roles | Cajero: vender, eliminar línea, reimprimir, consultar ventas (para buscar la venta de un cambio), crear clientes. Supervisor: además autoriza descuentos, precios, cancelar, anular, vencidos, cambios y ajuste rápido. Administrador: todo **menos** el reintegro por garantía |

Pruebas que pasan: `tests/Pos.Server.IntegrationTests/Phase7/SalesApiTests.cs` (venta completa) y `SalesFlowTests.cs` (existencias y
vencidos, suspender/cancelar/cierre, anulación, cambio y garantía). Escenario reutilizable: `Phase7/SalesScenario.cs`.

## 3. Qué falta (en este orden)

1. **Prueba de promociones** (integración): crear con el encargado, el cajero no puede (403), simular, activar, venta con 3×2, porcentaje por
   categoría, combo, promoción que termina mientras la venta está abierta, reporte `GET /promotions/report`.
2. **Pruebas unitarias** (proyectos nuevos, agregarlos a `Pos.slnx`): `tests/Pos.Modules.Sales.UnitTests` (calculadora con cada tipo de
   promoción, la más favorable sin acumular, prorrateo, redondeo; propiedad Σ líneas = total en 10.000 ventas aleatorias; `PaymentAllocator`;
   estados de `Sale`; `CustomerReturn.CreditFor`), `tests/Pos.Modules.Promotions.UnitTests` (validación, vigencia, días, horario nocturno,
   sucursales), `tests/Pos.Modules.Billing.UnitTests`, `tests/Pos.Printing.UnitTests` (bytes ESC/POS exactos, ajuste de líneas ≤ 42). El
   `build.ps1` ya exige cobertura ≥ 90 % en `Sales.Domain`, `Promotions.Domain`, `Billing.Domain` y `Pos.Printing`: **sin estas pruebas falla**.
3. **Pruebas de BD** (`tests/Pos.Database.Tests/SalesSchemaTests.cs`): número único por caja, una venta `OPEN` por caja, CHECK de pagado − cambio
   = total, cambio solo en efectivo, un cambio en borrador por venta, eventos fiscales de solo inserción.
4. **"Día de operación"** (`Phase7/DayOfOperationTests.cs`): fábrica Multicaja (`PosServerFactory`), crear 2 cajas más y emparejarlas con
   `SecurityScenario.PairTerminalAsync`, 3 cajeras, 500 ventas en paralelo con anulaciones y cambios, dos cajas por la última unidad (una gana),
   cerrar las 3 jornadas y verificar Σ ventas por medio = Σ caja por medio y `POST /inventory/verification` sin diferencias; medir p95.
5. **Agente de caja** `src/Terminal/Pos.Terminal.Agent` (servicio de Windows, .NET 10, escucha solo en `localhost:5490`): `POST /print`
   (recibe `TicketDocument` + configuración de la impresora), `POST /drawer/open`, `GET /status`, `POST /test-page`; transportes archivo, red
   `IP:9100`, spooler de Windows (RAW, `winspool.drv`) y puerto serie `COMx`. Usa `Pos.Printing`. Decisión tomada: el agente **no** se
   autentica con el servidor; la interfaz de caja lee `GET /organization/terminals/{id}/receipt-printer` y se la entrega (desviación a documentar).
6. **EndpointProtectionTests**: revisar que cubra los endpoints nuevos (`/sales`, `/exchanges`, `/promotions`, `/billing/documents`,
   `/inventory/quick-adjustments`, `/organization/terminals/{id}/receipt-printer`).
7. **`build.ps1` completo en verde** (todas las pruebas antiguas + nuevas + cobertura). Puntos que pueden romperse: pruebas de la Fase 4 que
   escanean un producto sin precio (ahora un producto con precio abierto no reporta "sin precio"); conteo de permisos en pruebas; lista de
   ensamblados de `ArchitectureTests` (ya se agregaron los nuevos y `Pos.Printing`).
8. **Documentación**: `http/fase-07.http`, ADR-0030 a 0036 (venta persistida y motor; existencias sin negativo y ajuste rápido; pagos y
   redondeo; anulación y cambios sin devolución de dinero; promociones; Billing con comprobante interno; agente de caja), notas en los docs 04,
   05 y 08, `fase-07-informe.md` (estructura igual a `fase-06-informe.md`), fila de la Fase 7 en `docs/README.md`.
9. Al terminar: `git switch main`, `git merge fase-7-ventas`, `git push`.

## 4. Decisiones de implementación a tener en cuenta

- Los números de ADR **0037–0039** están reservados para la Fase 12-A (portal de licencias).
- La promoción "lleve N pague M" se llama `MULTI_BUY` en código y BD (el conversor de enums no separa bien `BuyXPayY`).
- Una promoción se asigna **por línea** (no por unidad): la línea que participa en una promoción no recibe otra.
- `CustomerReturn` usa `received_by`/`received_at` (no `created_*`, que son columnas de control).
- Las FK cruzadas venta nueva ↔ cambio (`sales.exchange_id`, `customer_returns.replacement_sale_id`) son `DEFERRABLE INITIALLY DEFERRED`.
- Una venta pagada con crédito de cambio no se anula (`SALES.VOID_NOT_ALLOWED`).
- `TerminalInfo` (contrato de organización) ahora incluye `WarehouseId`.
- Los scripts de edición largos en Bash fallan con heredocs; se usaron scripts de Python en un archivo aparte.

## 5. Fase 12-A (portal de licencias)

Se construye en paralelo en la rama **`fase-12a-licencias`**, con su propia nota `docs/fases/fase-12a-avance.md` (en esa rama). Al unirla a
`main` revisar los archivos compartidos: `Pos.slnx`, `build.ps1`, `Directory.Packages.props`, `tests/Pos.ArchitectureTests/ArchitectureTests.cs`
y `docs/README.md`.
