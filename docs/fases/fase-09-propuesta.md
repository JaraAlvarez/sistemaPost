# Fase 9 · Reportes — Propuesta

> Estado: **APROBADA** (con las recomendaciones de la §14) · 2026-09-29
> Requisitos previos: Fases 4 a 8 implementadas (inventario, compras, caja, ventas con promociones y cambios, clientes y proveedores).
> Base: plan [12 §S, fase 9](../12-plan-riesgos-decisiones.md), docs [01 §1–3](../01-analisis-producto.md), [02 §13](../02-modulos.md),
> [06 permisos `reporting.*`](../06-seguridad-usuarios-permisos.md), [07 kardex](../07-inventario-kardex.md), ADR-0015 (sin planes por
> módulos: todos los reportes en ambas ediciones), ADR-0028 (fecha de negocio), ADR-0030 (costo del kardex en la venta).

**Convenciones:** 🔒 = decisión difícil de cambiar después (matriz en la §3). ✅ = regla que se cumple. ⚙️ = parámetro configurable.

## 0. Objetivo y alcance

Después de esta fase el dueño, el administrador y el contador **saben qué pasó en la tienda** sin abrir la base de datos: cuánto se
vendió y cómo (por día, hora, cajero, caja, medio de pago, producto, categoría, marca, cliente y lista), **cuánto se ganó** (utilidad y
margen con el costo real del kardex), **qué impuestos** se generaron y cuáles son descontables, cómo está el **inventario** (valorizado
hoy o a una fecha, bajo mínimo con sugerido de compra, sin rotación, vencimientos, ajustes), **compras, gastos y cartera por pagar**
(edades), la **caja** (cierres, diferencias, retiros) y un **reporte antifraude** por cajero. Todo se exporta a **Excel, CSV y PDF** y
hay un **tablero del día** con los indicadores principales.

Entregable verificable del plan: **los reportes cuadran entre sí y con los documentos**: Σ ventas por medio = Σ caja por medio;
Σ ventas por producto = Σ ventas del día; inventario valorizado = Σ saldos del kardex; utilidad = ventas netas − costo; IVA de ventas
por tarifa = Σ impuestos de las líneas.

| Incluido | Excluido (fase) |
|---|---|
| Módulo **`Reporting`** de solo lectura con un **catálogo de reportes** (parámetros, columnas, permiso) y un endpoint genérico | Consolidación de varias sucursales o empresas (sincronización / nube) |
| **Vistas de reporte** en el esquema `reporting` (contrato estable sobre las tablas de los módulos) | Reportes programados y envío por correo (posterior) |
| Ventas: resumen diario, por hora, cajero, caja, medio de pago, producto, categoría, marca, cliente, lista de precio; más y menos vendidos; descuentos y promociones | Diseñador de reportes por el usuario |
| **Utilidad y margen** por día, producto, categoría y cajero (costo del kardex, neto de cambios) | Contabilidad (causación, libros oficiales, NIIF) |
| **Impuestos**: generados en ventas por tipo y tarifa, descontables en compras, resumen para el contador; **libro de ventas diario** | Reportes para la DIAN (medios magnéticos) — posterior, con el contador |
| Inventario: valorizado **hoy o a una fecha**, bajo mínimo con **sugerido de compra**, sin rotación, próximos a vencer, ajustes por motivo | Pronóstico de demanda |
| Compras por proveedor, devoluciones a proveedor, **edades de cartera por pagar**, gastos por categoría | |
| Caja: cierres con diferencias, retiros, aperturas sin venta | |
| **Antifraude** por cajero: líneas eliminadas, ventas canceladas, anulaciones, descuentos, precios abiertos, cajón sin venta, lotes vencidos, ajustes rápidos, diferencias de caja | Alertas en tiempo real (notificaciones) |
| **Exportación** Excel (.xlsx), CSV y PDF con el encabezado de la empresa | Diseño visual de pantallas (15) |
| **Tablero del día** (ventas, tiquetes, ticket promedio, top productos, alertas de inventario y caja) | |

Se entrega en **cinco bloques** con una sola aprobación: **9.1 Motor, catálogo y exportación**, **9.2 Ventas, utilidad e impuestos**,
**9.3 Inventario, compras y gastos**, **9.4 Caja y antifraude**, **9.5 Tablero del día**.

---

## 1. Actores

| Actor (rol de sistema) | Qué ve |
|---|---|
| Propietario / Administrador | Todo, incluida la utilidad, los costos y el antifraude |
| Contador (`ACCOUNTANT`) | Ventas, impuestos, libro de ventas, inventario valorizado, compras, cartera, gastos, cierres de caja, utilidad (pregunta 4) |
| Supervisor de caja (`CASH_SUPERVISOR`) | Ventas básicas del día, cierres y antifraude **sin costos ni utilidad** |
| Inventario / Compras | Inventario (con costos si ya tienen `inventory.cost.view`), compras, sugerido de compra |
| Encargado de promociones | Ventas por producto y el reporte de promociones (ya existe) |
| Cajero | Nada nuevo (sus reportes X/Z siguen en la caja) |

## 2. Decisiones principales

| # | Decisión | Por qué | |
|---|---|---|---|
| D9-01 | **Módulo `Reporting` de solo lectura**: no escribe en ningún esquema de negocio; consulta con SQL (Dapper) en una conexión de **solo lectura** y con límite de tiempo por consulta ⚙️ (30 s) | Un reporte pesado nunca bloquea una venta; separación clara lectura/escritura (doc 03) | 🔒 |
| D9-02 | **Vistas del esquema `reporting`** (script repetible `R__reporting__views.sql`) como contrato: las consultas leen vistas (`reporting.sale_lines`, `reporting.payments`, `reporting.stock_movements`…), no las tablas de cada módulo | Los módulos pueden cambiar sus tablas sin romper los reportes (se ajusta la vista); una prueba verifica que todas las vistas compilan | 🔒 |
| D9-03 | **Catálogo de reportes en el código**: cada reporte declara código, nombre, grupo, parámetros (con validación), columnas (tipo, formato, total), permiso y SQL. Un solo endpoint `GET /reports/{code}?…&format=json|xlsx|csv|pdf` | Agregar un reporte = una clase; la exportación, los permisos y los totales son iguales para todos | 🔒 |
| D9-04 | **Fecha de negocio** (`business_date` de la jornada) para todo lo de ventas y caja; hora local de Colombia para "por hora"; rangos máximos ⚙️ (366 días) | Una venta de las 00:30 pertenece al turno que la hizo (ADR-0028); consistente con el Z | 🔒 |
| D9-05 | **Ventas netas** = ventas completadas − créditos de cambios − reintegros por garantía; las anuladas no cuentan (se listan aparte); el ajuste de redondeo del efectivo se muestra aparte | Un cambio no se cuenta dos veces (igual que el historial del cliente, D8-13) | 🔒 |
| D9-06 | **Utilidad con el costo del kardex** guardado en cada línea al cobrar (`unit_cost`), neto del costo de lo devuelto en cambios; margen = utilidad ÷ venta sin impuestos | Utilidad histórica exacta, sin recalcular con costos de hoy (D7-06) | |
| D9-07 | **Inventario a una fecha** desde el saldo del último movimiento de kardex ≤ fecha (el kardex guarda cantidad, valor y costo promedio resultantes) | No hace falta guardar fotos diarias; cuadra con la verificación saldo ↔ kardex | |
| D9-08 | **Consultas en vivo con índices** y límites (filas máximas ⚙️ 100.000; paginación en JSON); sin tablas de acumulados en esta fase (pregunta 1) | Una tienda genera ~1.000–3.000 ventas diarias: con índices por fecha de negocio las consultas de un mes son rápidas; los acumulados se agregan si el volumen lo pide sin cambiar la API | |
| D9-09 | **Exportación**: Excel con **MiniExcel** (ya en el proyecto, Apache-2.0), CSV nativo (separador `;`, UTF-8 con BOM para Excel en español) y PDF con **PDFsharp-MigraDoc** (MIT): tabla con encabezado de la empresa, parámetros, fecha de generación y totales | Solo licencias permitidas (doc licencias-terceros); QuestPDF no se usa por su licencia comercial | |
| D9-10 | **Permisos** del doc 06 ajustados: `reporting.sales.basic`, `reporting.sales.advanced`, `reporting.profit.view` (sensible), `reporting.taxes.view`, `reporting.inventory.view`, `reporting.purchases.view`, `reporting.cash.view`, `reporting.antifraud.view` (sensible) y `reporting.export`; los costos se ocultan sin `inventory.cost.view` | Utilidad y costos son información sensible (pregunta 4) | |
| D9-11 | **Antifraude** como reporte por cajero y período con indicadores (conteos y valores) y **umbrales** ⚙️ que resaltan al cajero fuera de lo normal respecto al promedio de la tienda; cada fila lleva al detalle (venta, línea, autorización) | Riesgo 7 del doc 01: detectar patrones, no solo listar eventos | |
| D9-12 | **Cada exportación queda en la auditoría** (quién, qué reporte, parámetros, filas) | Los reportes con costos y datos de clientes son sensibles (Ley 1581) | |
| D9-13 | **Solo los datos de este nodo** (su sucursal); la consolidación de sucursales llega con la nube | En Multicaja cada tienda tiene su servidor y su BD (ADR-0015); la nube tendrá la vista consolidada | |

## 3. Revisión arquitectónica de las decisiones difíciles de cambiar

Impacto en licenciamiento: **ninguno** (ADR-0015: todos los reportes en ambas ediciones; la fila "Reportes avanzados" del doc 09
original quedó reemplazada).

| ID | Decisión | Motivo | Alternativas | Consecuencia positiva | Riesgo | Impacto POS local | Impacto multisucursal | Impacto sincronización | Dificultad de cambiar | Recomendación |
|---|---|---|---|---|---|---|---|---|---|---|
| D9-01 | Módulo de solo lectura, conexión de solo lectura con tiempo límite | Nunca afectar la venta | Consultas dentro de cada módulo | Aislamiento; se puede mover a una réplica | Un reporte lento cortado a los 30 s | Mismo servidor; `statement_timeout` protege la caja | Igual en cada tienda | En la nube el mismo módulo leerá la BD consolidada | Media | **Adoptar** |
| D9-02 | Vistas `reporting.*` como contrato | Desacoplar reportes de tablas | Leer tablas directo; proyecciones propias | Cambios de tabla sin romper reportes | Mantener vistas al día (prueba) | Vistas sin costo de almacenamiento | — | La nube expone las mismas vistas | Alta (todo el SQL de reportes depende) | **Adoptar** |
| D9-03 | Catálogo en código + endpoint genérico | Uniformidad y exportación única | Un endpoint por reporte | Exportar, paginar, totalizar y proteger igual para todos | Menos flexibilidad por reporte | — | — | — | Media | **Adoptar** |
| D9-04 | Fecha de negocio para ventas y caja | Coincidir con el Z | Fecha calendario de la hora | Los reportes cuadran con los cierres | Diferencias de un día contra la hora del reloj (se explica en el reporte) | — | — | Mismo criterio en la nube | Alta | **Adoptar** |
| D9-05 | Ventas netas de cambios y garantías | No duplicar | Contar bruto | Totales coherentes con el historial del cliente | — | — | — | — | Alta | **Adoptar** |

## 4. Modelo de datos

Sin tablas de negocio nuevas.

| Script | Contenido |
|---|---|
| `V2026.10.027__reporting__schema.sql` | Esquema `reporting`; rol de BD **`pos_report`** (solo `SELECT` sobre `reporting`, `statement_timeout` ⚙️); índices faltantes para reportes (p. ej. `sale_lines(product_id)` con la venta por fecha, `cash_movements(movement_type)`, `stock_movements(branch_id, business_date)`) |
| `R__reporting__views.sql` (repetible) | Vistas: `sales`, `sale_lines`, `sale_line_taxes`, `payments`, `returns`, `stock_balances`, `stock_movements`, `purchases`, `purchase_lines`, `payables`, `expenses`, `cash_sessions`, `cash_movements`, `antifraud_events` (unión de eventos sensibles de ventas, caja e inventario), con nombres de producto, categoría, marca, cajero, caja y cliente ya resueltos |
| `A__system__privileges.sql` | Privilegios del rol `pos_report`; `pos_app` no escribe en `reporting` |

## 5. Catálogo de reportes

| Grupo | Código | Contenido | Permiso |
|---|---|---|---|
| Ventas | `SALES_DAILY` | Por fecha de negocio: tiquetes, bruto, promociones, descuentos, base, impuestos, redondeo, total, cambios, neto, anuladas | `sales.basic` |
| | `SALES_BY_HOUR` | Tiquetes y ventas por hora del día (hora local) | `sales.basic` |
| | `SALES_BY_PAYMENT` | Por medio de pago (cuadra con la caja por medio) | `sales.basic` |
| | `SALES_BY_CASHIER` · `SALES_BY_TERMINAL` | Ventas, tiquetes, ticket promedio, unidades | `sales.basic` |
| | `SALES_BY_PRODUCT` · `_CATEGORY` · `_BRAND` | Unidades, ventas netas; más y menos vendidos (orden) | `sales.advanced` |
| | `SALES_BY_CUSTOMER` · `SALES_BY_PRICE_LIST` | Clientes identificados, lista y grupo (solo con autorización de datos para el detalle por cliente) | `sales.advanced` |
| | `DISCOUNTS_AND_PROMOTIONS` | Descuentos manuales (con quién autorizó) y promociones aplicadas | `sales.advanced` |
| Utilidad | `PROFIT_BY_DAY` · `_PRODUCT` · `_CATEGORY` · `_CASHIER` | Venta neta sin impuestos, costo, utilidad, margen | `profit.view` |
| Impuestos | `TAXES_SALES` | IVA e impuestos al consumo generados por tipo y tarifa (base y valor) | `taxes.view` |
| | `TAXES_PURCHASES` | IVA descontable de compras contabilizadas (según el setting de la Fase 5) | `taxes.view` |
| | `SALES_BOOK` | **Libro de ventas diario**: por día y caja, número inicial y final, base y valor por tarifa, excluidos/exentos, total, medios | `taxes.view` |
| Inventario | `INVENTORY_VALUATION` | Por bodega y categoría, hoy o **a una fecha** (costos solo con `inventory.cost.view`) | `inventory.view` |
| | `BELOW_MINIMUM` | Bajo mínimo con **sugerido de compra** = máximo − (saldo + pedido pendiente) y proveedor preferido | `inventory.view` |
| | `NO_ROTATION` | Sin ventas en N días ⚙️ (30) con saldo y valor inmovilizado | `inventory.view` |
| | `EXPIRING_LOTS` | Próximos a vencer y vencidos con valor | `inventory.view` |
| | `ADJUSTMENTS` | Ajustes por motivo (incluidos los rápidos de la caja) con valor | `inventory.view` |
| Compras | `PURCHASES_BY_SUPPLIER` · `SUPPLIER_RETURNS` | Compras y devoluciones por proveedor y período | `purchases.view` |
| | `PAYABLES_AGING` | Edades de cartera por pagar (al día, 1–30, 31–60, 61–90, > 90) | `purchases.view` |
| | `EXPENSES_BY_CATEGORY` | Gastos por categoría y período | `purchases.view` |
| Caja | `CASH_CLOSINGS` | Jornadas cerradas: esperado, contado y diferencia por medio; revisadas o no | `cash.view` |
| | `CASH_WITHDRAWALS` | Retiros, ingresos y aperturas sin venta con quién autorizó | `cash.view` |
| Antifraude | `ANTIFRAUD_BY_CASHIER` | Por cajero: líneas eliminadas, cancelaciones, anulaciones, descuentos, precios abiertos, cajón sin venta, vencidos, ajustes rápidos y diferencias, con valor y comparación con el promedio | `antifraud.view` |
| | `ANTIFRAUD_EVENTS` | Detalle de cada evento con su venta, caja y autorización | `antifraud.view` |

Existentes que se enlazan (no se duplican): reportes X/Z de caja, kardex, reporte de promociones, historial del cliente, vencimientos
de cartera y resumen de proveedor.

## 6. Flujos

- `GET /reports` → catálogo visible para el usuario (solo los reportes de sus permisos) con parámetros y columnas.
- `GET /reports/{code}?from=&to=&branchId=&…&page=` → JSON con filas, totales y metadatos (parámetros usados, generado, filas).
- `GET /reports/{code}?…&format=xlsx|csv|pdf` → archivo (exige `reporting.export`); queda en la auditoría (D9-12).
- `GET /reports/dashboard` → tablero del día: ventas y tiquetes de hoy vs. el mismo día de la semana anterior, ticket promedio, top 10
  productos, ventas por hora, cajas abiertas, cierres con diferencia sin revisar, productos bajo mínimo, lotes por vencer, solicitudes de
  titulares por vencer (sin datos de costo si no tiene el permiso).

## 7. Reglas

| Regla | Implementación |
|---|---|
| RN-REP-01 Fecha de negocio | Ventas y caja por `business_date`; hora local de Colombia para la distribución por hora |
| RN-REP-02 Ventas netas | Completadas − créditos de cambios − reintegros; anuladas aparte |
| RN-REP-03 Rangos | Máximo ⚙️ 366 días; filas máximas ⚙️ 100.000 (el archivo avisa si se cortó) |
| RN-REP-04 Costos | Utilidad y costos solo con `reporting.profit.view` / `inventory.cost.view` |
| RN-REP-05 Datos personales | Detalle por cliente solo de clientes con autorización SERVICE (Ley 1581, D8-07) |
| RN-REP-06 Exportación auditada | Quién, reporte, parámetros y filas |
| RN-REP-07 Cuadre | Las pruebas verifican las igualdades del entregable (§0) |

## 8. Permisos y roles

| Permiso | Roles de sistema |
|---|---|
| `reporting.sales.basic` | CASH_SUPERVISOR, ACCOUNTANT, PROMOTIONS_MANAGER, OWNER, ADMIN |
| `reporting.sales.advanced` | ACCOUNTANT, PROMOTIONS_MANAGER, OWNER, ADMIN |
| `reporting.profit.view` — sensible | ACCOUNTANT (pregunta 4), OWNER, ADMIN |
| `reporting.taxes.view` | ACCOUNTANT, OWNER, ADMIN |
| `reporting.inventory.view` | INVENTORY, PURCHASING, ACCOUNTANT, OWNER, ADMIN |
| `reporting.purchases.view` | PURCHASING, ACCOUNTANT, OWNER, ADMIN |
| `reporting.cash.view` | CASH_SUPERVISOR, ACCOUNTANT, OWNER, ADMIN |
| `reporting.antifraud.view` — sensible | CASH_SUPERVISOR, OWNER, ADMIN |
| `reporting.export` | ACCOUNTANT, OWNER, ADMIN (+ quien lo reciba por excepción) |

## 9. Impacto en la sincronización

Ninguno en esta fase: los reportes solo leen. Las vistas `reporting.*` son el contrato que la nube reutilizará para la consolidación
de sucursales.

## 10. Validación de la fase (según tu forma de trabajo)

Se construye y se verifica la **coherencia**: compilación sin advertencias, las migraciones y **todas las vistas** se crean en una BD
nueva, cada reporte del catálogo se ejecuta sin error contra el escenario de ventas existente y las **igualdades de cuadre** de la §0
se comprueban en una prueba corta. Las pruebas funcionales de cada reporte las haces tú con `http/fase-09.http` y los archivos
exportados; el informe dirá qué quedó sin probar.

## 11. Riesgos

| Riesgo | Mitigación |
|---|---|
| Un reporte pesado en hora pico hace lenta la caja | Rol de solo lectura con `statement_timeout`, límites de rango y filas, índices; en el futuro réplica o acumulados |
| Los números del reporte no coinciden con lo que el contador espera (IVA, base) | Libro de ventas y reporte de impuestos revisados con tu contador; misma base de las líneas de venta |
| Fuga de información sensible (costos, datos de clientes) en exportaciones | Permisos separados, ocultar costos, clientes solo con autorización, exportación auditada |
| Diferencias por fecha de negocio vs. reloj | El reporte explica que se agrupa por jornada; "por hora" usa la hora real |
| Cambios de tablas rompen reportes | Vistas como contrato + prueba que las compila |

## 12. Estructura de código

```
src/Modules/Reporting/*        (nuevo) Catálogo de reportes, motor (parámetros, SQL, totales, paginación), exportadores
                               (Excel, CSV, PDF), tablero del día; Contracts: permisos
src/Server/Pos.Server.Migrations  V027, R__reporting__views.sql, privilegios del rol pos_report
src/Modules/Identity           Roles: permisos de reportes
http/fase-09.http
```

## 13. Criterios de aceptación

- [ ] Catálogo de reportes con los de la §5; endpoint genérico con JSON paginado y exportación a Excel, CSV y PDF.
- [ ] Las igualdades de cuadre de la §0 se cumplen en el escenario de ventas de la Fase 7 y 8.
- [ ] Inventario a una fecha igual al saldo del kardex a esa fecha; sugerido de compra con mínimos y máximos.
- [ ] Antifraude por cajero con umbrales ⚙️ y detalle de eventos.
- [ ] Tablero del día.
- [ ] Permisos por reporte; costos y utilidad ocultos sin permiso; exportaciones auditadas.
- [ ] Rol de BD de solo lectura con tiempo límite; vistas del esquema `reporting` verificadas.
- [ ] `dotnet build` sin advertencias; docs 02, 06 y 07 actualizados; ADRs (módulo de solo lectura y vistas como contrato; catálogo y
      exportación; ventas netas y fecha de negocio) e informe.

## 14. Preguntas para ti (la opción recomendada va primero)

1. **Rendimiento:** ¿reportes **en vivo** con índices y límites (**recomendado**; los acumulados nocturnos se agregan solo si una
   tienda muy grande lo necesita), o acumulados diarios desde ya?
2. **PDF:** ¿tabla sencilla con el encabezado de la empresa, parámetros y totales (**recomendado**), o un diseño con logo y gráficos
   (más trabajo; mejor en la fase de diseño visual)?
3. **Antifraude:** ¿panel con umbrales configurables que resalta cajeros fuera de lo normal, sin notificaciones por ahora
   (**recomendado**), o quieres alertas inmediatas (requiere un canal: correo, WhatsApp)?
4. **Utilidad y costos:** ¿los ve el contador además del propietario y el administrador (**recomendado**), o solo propietario y
   administrador?
5. **Libro de ventas diario para el contador:** ¿lo incluimos con el formato estándar (por día y caja: consecutivos, base e IVA por
   tarifa, totales) (**recomendado**, validándolo con tu contador), o tu contador ya tiene un formato que debamos seguir?
6. **Varias sucursales:** ¿cada tienda ve sus reportes y la **consolidación** llega con la nube (**recomendado**), o necesitas ya un
   reporte que junte sucursales exportando e importando archivos?

**Resolución (aprobación del propietario):** se adoptan las seis recomendaciones: reportes en vivo con límites; PDF de tabla sencilla
con el encabezado de la empresa; antifraude como panel con umbrales, sin notificaciones; el contador ve utilidad y costos; libro de
ventas diario con el formato estándar (a validar con el contador); cada tienda ve sus reportes y la consolidación llega con la nube.
