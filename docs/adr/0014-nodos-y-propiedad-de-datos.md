# ADR-0014 · Nodos, propiedad de datos y convenciones de sincronización

- **Estado:** Aceptada · 2026-09-28 · Fase 2

## Contexto
El producto funcionará offline en cada tienda, con varias cajas y equipos administrativos, y sincronizará con una nube donde el portal web también edita datos maestros (decisiones del propietario, [revisión §9–§10](../fases/fase-02-revision-arquitectonica.md)).

## Decisión
- **Una BD por nodo** (`org.nodes`: tienda, Caja Única, caja autónoma, nube); `installation_id` = id del nodo.
- **Documentos**: un solo escritor (el nodo de la sucursal), inmutables; `origin_node_id` en toda tabla de documentos desde la Fase 4.
- **Maestros** (productos, precios, clientes, usuarios…): editables en tiendas y portal; `row_version` lógico; los eventos llevan solo los campos modificados y su versión base; mismo campo en conflicto → gana el más reciente y queda en una bandeja de conflictos.
- **Saldos** (stock, crédito, puntos) nunca se sincronizan: se derivan de movimientos.
- Unicidad en el **ámbito de quien crea** (p. ej. bodegas únicas por sucursal).
- Outbox con `node_seq` (lo confirmado por la nube), inbox idempotente y cursores. **Internet y paquete `.possync` son el mismo mecanismo** con dos transportes.
- Asistente con dos modos: crear empresa (v1) y unirse a una empresa existente (con la sincronización).

## Consecuencias
- ✅ La sincronización no exige rehacer datos: las convenciones existen desde la primera tabla.
- ⚠️ Crear productos en varias tiendas sin conexión puede duplicar códigos de barras: se resuelve con fusión en la nube.
