# ADR-0040 · Módulo Customers y rol de cliente con la clave del tercero

- **Estado:** Aceptada · 2026-09-29 · Fase 8 · Decisiones D8-01, D8-02 y D8-04

## Contexto
La venta ya admitía un tercero como cliente (Fase 7), pero sin reglas comerciales (grupo, lista, bloqueo), sin privacidad de datos y
con la cajera pudiendo modificar cualquier tercero, incluidos los proveedores.

## Decisión
- **Módulo nuevo `Customers`** (esquema `customers`) dueño del rol cliente, los grupos, la privacidad y, en la 8-B, el crédito y los
  puntos. `Parties` sigue siendo solo la identidad (igual que `Purchasing` es dueño del rol proveedor, ADR-0023).
- **El rol usa la clave del tercero** (`customers.customers.party_id` es la PK). Si dos tiendas crean el rol sin conexión convergen
  en la misma fila. El rol nace en el alta rápida o la primera vez que un tercero compra (`AUTO_ON_SALE`).
- **Alta rápida sin duplicados:** `IPartyRegistry.RegisterAsync` toma un bloqueo transaccional por identificación
  (`pg_advisory_xact_lock`): dos cajas que crean la misma cédula a la vez reciben el mismo tercero. Si la identificación ya existe,
  se devuelve el tercero **sin modificarlo**. Mismo número con otro tipo (CC vs NIT) → advertencia de posible duplicado.
- **La cajera no edita terceros:** se retira `parties.party.manage` de CAJERO y SUPERVISOR; la cajera crea clientes y completa
  **solo campos vacíos**; el supervisor corrige (`customers.customer.manage`).
- Las FK de `customers` y `customer_consents` al tercero son **diferidas** (el alta crea tercero, rol y autorización en una transacción).

## Consecuencias
- Historial y ventas se consultan por `party_id` sin tabla intermedia.
- Los roles personalizados clonados conservan `parties.party.manage`: hay que revisarlos a mano.
- El rol de los terceros que ya compraron antes de la Fase 8 se crea en su siguiente compra (no hay migración masiva).
