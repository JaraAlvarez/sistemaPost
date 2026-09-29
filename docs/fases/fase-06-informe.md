# Fase 6 · Caja: jornadas, movimientos, arqueos y cierres — Informe

> Estado: **IMPLEMENTADA — pendiente de tu validación** · 2026-09-29
> Documentos: [propuesta aprobada](fase-06-propuesta.md) · ADR [0027](../adr/0027-movimientos-de-caja-de-solo-insercion.md) ·
> [0028](../adr/0028-fecha-de-negocio-y-cierre-definitivo.md) · [0029](../adr/0029-sello-de-auditoria-en-el-reporte-z.md)

## 1. Resultado frente a los criterios de aceptación (§14)

| Criterio | Resultado | Evidencia |
|---|---|---|
| Una jornada por caja y por cajero, incluso con aperturas simultáneas | ✅ | Índices únicos parciales; `Dos_aperturas_simultaneas_de_la_misma_caja_dejan_una_sola_jornada` (una 201 y una 409); prueba en la BD real |
| Movimientos de solo inserción; esperado siempre calculado; arqueo ciego | ✅ | Privilegios + disparador (ni el dueño de las tablas los borra); dirección fijada por el tipo (CHECK); la cajera no ve lo esperado ni el reporte X (`CASH.BLIND_COUNT`) |
| Retiros y aperturas sin venta con autorización de supervisor registrada | ✅ | La cajera recibe `AUTH.AUTHORIZATION_REQUIRED`, el supervisor autoriza con su código y PIN (un solo uso) y `authorized_by` queda en el movimiento y en la auditoría; un retiro no deja el efectivo negativo (`CASH.INSUFFICIENT_CASH`) |
| Gastos y pagos a proveedores desde la caja en una transacción con su movimiento | ✅ | Gasto menor (`/expenses/from-cash`), pago de cartera y compra de contado con `cashSessionId`; anularlos con la jornada abierta devuelve el dinero con una corrección; con la jornada cerrada no se puede |
| Cierre en dos pasos, diferencias con umbral y revisión; cierre por supervisor | ✅ | `OPEN → CLOSING → CLOSED`; faltante de $10.000 sin observación → 400, con observación → pendiente de revisión; el cajero no revisa su propio cierre; cierre por supervisor con motivo, auditado como crítico |
| Reporte X y Z (texto 80 mm y JSON); el Z lleva un sello que `verify-audit` reconoce | ✅ | Z con `SELLO #N · XXXX-XXXX-XXXX-XXXX`; `GET /audit/seals/{n}/check` confirma el código (y rechaza otro); `verify-audit --seal N --code …` en el migrador; todas las líneas del texto ≤ 42 columnas |
| RN-SEC-07 completa; permisos en todos los endpoints; auditoría | ✅ | Desactivar a la cajera con jornada abierta → `IDENTITY.USER_HAS_OPEN_SESSION`; tras el cierre por supervisor sí se desactiva |
| Turno que pasa la medianoche conserva su fecha | ✅ | La fecha de negocio se fija al abrir (zona de Bogotá); prueba unitaria con una apertura a las 23:30 |
| `build.ps1` en verde; cobertura del dominio de caja ≥ 90 % | ✅ | Ver §3 |
| Docs 04, 05 y 08, ADRs e informe | ✅ | ADR-0027 a 0029; notas de implementación en 04, 05 y 08; `http/fase-06.http` |

## 2. Qué se construyó

```
src/Server/Pos.Server.Migrations   V014 caja (denominaciones, jornadas, movimientos de solo inserción, conteos, totales) · V015 gastos
                                   8 permisos · movimientos de caja solo SELECT/INSERT · FK del pago a proveedor a la jornada
src/BuildingBlocks
  Pos.Application.Abstractions     IAuditAnchor (sellado a demanda para el Z)
  Pos.Infrastructure               AuditSealer: ciclo protegido contra concurrencia, último sello; AuditVerifier.CheckSealCodeAsync
src/Modules/Cash                   Jornadas, movimientos (ICashRegister para otros módulos), conteos por denominación, cierre ciego,
                                   cierre por supervisor, revisión, reportes X y Z, denominaciones de COP
src/Modules/Expenses (nuevo)       Gastos y categorías de dos niveles; gastos desde la caja y su anulación
src/Modules/Purchasing             Pagos a proveedor y compras de contado desde la caja; anular un pago de caja devuelve el dinero
src/Modules/Identity               RN-SEC-07; roles: cajero (operar su caja), supervisor de caja (retiros, cajón, cierres, revisión, reportes)
src/Modules/Audit                  GET /audit/seals/{n}/check
src/Server/Pos.Server.Migrator     verify-audit --seal N --code XXXX-XXXX-XXXX-XXXX
http/fase-06.http
```

Datos iniciales de cada empresa (también en las instalaciones existentes, al arrancar): 11 denominaciones de COP (billetes de
$100.000 a $2.000 y monedas de $1.000 a $50) y las categorías de gasto (servicios públicos con energía, agua, gas e internet;
arriendo; aseo y cafetería; transporte y fletes; papelería; mantenimiento; otros).

## 3. Pruebas y cobertura

| Proyecto | Pruebas | Qué cubre |
|---|---|---|
| Pos.ArchitectureTests | 21 | R1–R8 con los 5 ensamblados de gastos |
| Pos.Database.Tests | 58 | + una jornada sin cerrar por caja y por cajero, cierre con totales, el cajero no revisa su cierre, movimientos de caja de solo inserción con dirección y valor fijados por el tipo |
| Pos.Modules.Cash.UnitTests | 11 | Esperado por medio con todos los tipos, cierre ciego con diferencia, observación y revisión, cierre por supervisor, conteo por denominación, turno nocturno, reporte Z de 80 mm |
| Pos.Modules.Expenses.UnitTests *(nuevo)* | 8 | Categorías de dos niveles, gastos válidos e inválidos, anulación |
| Pos.Server.IntegrationTests | 100 | + 3 escenarios de la Fase 6 (turno completo con sello verificado, cierre por supervisor y RN-SEC-07, aperturas simultáneas) |
| Demás proyectos | 283 | Sin cambios de la Fase 5 |
| **Total** | **481** | Fase 5: 460 |

Cobertura de líneas (combinada entre proyectos):

| Ensamblado | Cobertura | Mínimo |
|---|---|---|
| Pos.Modules.Cash.Domain | 99,3 % | 90 % ✅ |
| Pos.Modules.Expenses.Domain | 100 % | 90 % ✅ |
| Pos.Infrastructure / Pos.Server.Migrations / Pos.SharedKernel | 94,9 % / 93,7 % / 99,6 % | 85 / 85 / 95 % ✅ |
| Cash.Api / Cash.Application / Cash.Infrastructure | 98,9 % / 91,2 % / 100 % | — |
| Expenses.Api / Expenses.Application / Expenses.Infrastructure | 97 % / 76 % / 94,3 % | — (ramas de error poco frecuentes) |
| Audit (Api / Application / Infrastructure) | 95,5 % / 100 % / 100 % | — |

## 4. Desviaciones respecto de la propuesta (y por qué)

| # | Propuesta | Implementado | Motivo |
|---|---|---|---|
| 1 | Tipo de documento `CASH_MOVEMENT` | Los movimientos se numeran por jornada (`line_no`), sin serie propia | El número de la jornada + la línea identifica cada movimiento; una serie más no aporta control |
| 2 | "Forzar un sellado" al cerrar | Se ejecuta un ciclo del sellador y el Z imprime el **último sello** del nodo | Sellar lo recién escrito rompería el horizonte seguro (ADR-0012): el ancla cubre lo ocurrido hasta ~1 minuto antes del cierre y la cadena protege lo posterior |
| 3 | Pagos desde la caja | Solo con un medio que afecta el cajón (efectivo) | Un pago con transferencia no sale del cajón: se registra como pago normal de cartera |
| 4 | Anular un gasto o pago de caja | Con la jornada abierta vuelve con una corrección; con la jornada cerrada se rechaza | El cierre es definitivo (RN-CSH-08): la corrección se registra en la jornada actual |
| 5 | Compra de contado "desde la caja" | Opcional: `cashSessionId` al contabilizar; sin él sigue siendo un pago fuera de caja | La administración también paga en efectivo desde la caja fuerte |
| 6 | Alerta de jornada abierta > N horas | Configuración `cash.open_session_alert_hours` (14) publicada; la alerta en pantalla llega con la UI | Sin interfaz no hay dónde mostrarla |

## 5. Limitaciones conocidas y pendiente

1. **Ventas** (movimientos `SALE`, `SALE_VOID`, `CUSTOMER_REFUND`): la Fase 7 los registra con `ICashRegister`; hoy el cierre
   ya exige `CLOSING` para bloquear ventas nuevas.
2. **Apertura física del cajón e impresión** del Z: agente de caja (Fase 7).
3. **Conciliación de datáfonos**: fuera del producto; el cierre compara por medio de pago.
4. Pendientes externos: validación del Servicio de Windows de la Fase 1 y consulta a Factus antes de la Fase 7.

## 6. Cómo validarlo

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1          # compila, pruebas y cobertura → BUILD OK
powershell -ExecutionPolicy Bypass -File .\tools\scripts\dev-db.ps1   # aplica las migraciones 014–015
dotnet run --project src\Server\Pos.Server.Host              # http://localhost:5480
```

Luego ejecuta `http/fase-06.http` en orden (en Multicaja, empareja antes el equipo como caja). Verificación del sello impreso:

```powershell
dotnet run --project src\Server\Pos.Server.Migrator -- verify-audit --seal 1234 --code 7F3A-91C2-0B44-E1D8 --connection "Host=127.0.0.1;Port=5488;Database=pos;Username=pos_app;Password=pos-dev-app-password"
```
