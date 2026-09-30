# ADR-0044 · Módulo Reporting de solo lectura y vistas `reporting.*` como contrato

- **Estado:** Aceptada · 2026-09-29 · Fase 9 · Decisiones D9-01, D9-02, D9-08 y D9-13

## Contexto
Los reportes cruzan ventas, caja, inventario, compras, gastos y clientes. Si cada reporte leyera las tablas de cada módulo, cualquier
cambio de tabla rompería reportes, y una consulta pesada en hora pico podría frenar la caja, que usa la misma base de datos.

## Decisión
- **Módulo `Reporting` sin dominio ni tablas propias**: no escribe en ningún esquema de negocio.
- **Vistas del esquema `reporting`** (script repetible `R__reporting__views.sql`, 31 vistas) como contrato: `sales`, `sale_lines`,
  `sale_line_taxes`, `payments`, `discounts`, `returns`, `return_lines`, `products`, `categories`, `brands`, `stock_balances`,
  `stock_movements`, `stock_policies`, `lots`, `inventory_adjustments`, `pending_orders`, `purchases`, `purchase_lines`,
  `purchase_line_taxes`, `supplier_returns`, `supplier_products`, `payables`, `expenses`, `cash_sessions`, `cash_session_totals`,
  `cash_movements`, `antifraud_events`, `customer_consents`, `data_requests`, `branches` y `users`, con nombres ya resueltos.
  Los reportes solo leen estas vistas; si un módulo cambia sus tablas se ajusta la vista. Una prueba de BD verifica que todas compilan.
- **Consultas en una transacción de SOLO LECTURA** (`SET TRANSACTION READ ONLY`, aislamiento REPEATABLE READ) **con
  `statement_timeout` local** (⚙️ `reporting.statement_timeout_seconds`, 30 s), en una conexión propia. `pos_app` solo tiene `SELECT`
  sobre el esquema `reporting`.
- **Consultas en vivo con índices y límites** (⚙️ 366 días de rango, ⚙️ 100.000 filas; el resultado avisa si se cortó); sin tablas de
  acumulados en esta fase. V027 agrega los índices por fecha de negocio.
- **Solo los datos del nodo** (empresa y sucursal de la instalación): la consolidación de sucursales llega con la nube, que reutilizará
  las mismas vistas.

## Desviación respecto a la propuesta
La propuesta mencionaba un rol de BD aparte (`pos_report`). Se implementó con el rol de la aplicación más la transacción de solo
lectura y el tiempo límite: el efecto es el mismo (la BD rechaza cualquier escritura del reporte y lo corta si tarda) y se evita
administrar una tercera contraseña de BD en cada instalación. Si en el futuro los reportes se leen desde una réplica, se agrega el rol.

## Consecuencias
- Un reporte lento se corta con `REPORTING.TIMEOUT` (422) sin afectar la caja.
- Cambiar una tabla de un módulo exige revisar las vistas que la usan (la prueba `ReportingSchemaTests` falla si una vista no compila).
- Los reportes no ven datos de otras sucursales hasta la nube.
