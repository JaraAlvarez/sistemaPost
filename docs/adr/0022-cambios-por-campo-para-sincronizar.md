# ADR-0022 · Registro de cambios por campo de los maestros sincronizables

- **Estado:** Aceptada · 2026-09-28 · Fase 4 · Decisión D4-09

## Contexto
Productos, precios y demás maestros se editan en cualquier tienda y en el portal (decisión del propietario). La revisión de la
Fase 2 (§10.1) definió que ediciones de campos distintos se combinan y que el mismo campo cambiado en dos lugares es un
conflicto visible. Para eso cada cambio debe viajar con los campos modificados y la versión de la que partió.

## Decisión
- El interceptor de guardado genera, en la **misma transacción**, un mensaje `SYNC` de tipo `sync.entity_changed.v1` por cada
  entidad `ISyncVersioned` creada, modificada o borrada lógicamente, con: entidad (`esquema.tabla`), id, operación,
  `baseVersion`, `version`, nodo y **solo** los campos modificados (en su forma de la BD).
- El estado local (`[LocalOnly]`) y las columnas de control no viajan. Aplica a todos los maestros (también Organization e
  Identity), no solo al catálogo.
- Los movimientos de inventario viajan aparte como `inventory.movements_posted.v1` (documentos, sin conflictos).

## Consecuencias
- ✅ La fase de sincronización recibe desde ya un historial de cambios completo y ordenado (`node_seq`).
- ⚠️ Más filas en el outbox (una por cambio); se depuran cuando la nube confirma (fase de sincronización).
- ⚠️ Los hijos "owned" (roles de un usuario) aún no generan su propio evento; se resolverá al diseñar la sincronización.
