# ADR-0019 · Kardex inmutable y costo promedio ponderado por bodega con valor

- **Estado:** Aceptada · 2026-09-28 · Fase 4 · Decisiones D4-01, D4-02, D4-03 y D4-04

## Contexto
El inventario debe explicarse siempre por documentos (RN-INV-01/02/11), cuadrar al centavo y soportar varias cajas
publicando a la vez. La tienda trabaja sin Internet y la nube consolidará los movimientos de todas las sucursales.

## Decisión
- `inventory.stock_movements` es de **solo inserción**: `pos_app` no tiene UPDATE/DELETE y un disparador lo impide incluso al
  dueño de las tablas. Un error se corrige con un movimiento inverso.
- `inventory.stock_balances` es un **caché** del kardex, actualizado en la **misma transacción** que el movimiento, con la fila
  bloqueada (`FOR UPDATE`) en orden (producto, bodega): sin *deadlocks* entre cajas.
- Un único punto de entrada: `IInventoryPosting` (contrato del módulo Inventory). La prueba de arquitectura R8 impide que otro
  módulo escriba esas tablas.
- **Costo promedio ponderado por bodega** llevando el **valor total** del saldo: promedio = valor ÷ cantidad. Las entradas
  valorizadas cambian el promedio; ajustes y salidas usan el vigente; la salida que deja el saldo en cero lleva el valor
  restante (sin residuos). Con saldo ≤ 0, la siguiente entrada fija el promedio en su costo.
- Cantidades siempre en **unidad base** (`numeric(18,4)`); la unidad base y el tipo del producto no cambian si hay movimientos.
- Cada bodega tiene **un solo nodo escritor** (el de su sucursal); el portal no mueve inventario.
- `seq` (identidad) ordena el kardex; por bodega y producto es creciente en el orden de confirmación, lo que permite contar
  sin cerrar la tienda (teórico congelado + movimientos posteriores).
- Verificación diaria y manual (`verify-stock`): una diferencia es un incidente **crítico** en la auditoría; la
  reconstrucción (`rebuild-stock`) es explícita, con motivo y auditada.

## Consecuencias
- ✅ Kardex auditable y reconstruible; 30 publicaciones simultáneas sin bloqueos mutuos; 10.000 movimientos aleatorios sin
  diferencia de valor.
- ✅ La sincronización solo sube movimientos (UUID, sin conflictos); la nube recalcula saldos.
- ⚠️ Una fila de saldo muy concurrida serializa sus publicaciones (transacciones cortas; medido).
- ⚠️ Cambiar el método de costeo después obligaría a recalcular el historial.
