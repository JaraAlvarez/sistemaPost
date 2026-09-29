# ADR-0031 · Existencias sin saldo negativo y ajuste rápido autorizado

- **Estado:** Aceptada · 2026-09-29 · Fase 7 · Decisiones D7-05, D7-06 y D7-15 (preguntas 2 y 8 del propietario)

## Contexto
El propietario vende solo productos físicos y quiere existencias confiables: no se vende lo que el sistema no tiene. Pero el sistema
puede estar mal (una compra sin registrar, un conteo errado) y el cliente espera en la fila con el producto en la mano. Además, los
lotes vencidos con saldo deben retirarse, no venderse sin control.

## Decisión
- **No se vende sin existencias** (productos que manejan inventario): al escanear se compara con el disponible de la bodega de la caja
  y la línea no se agrega (`SALES.INSUFFICIENT_STOCK`); al cobrar se verifica de nuevo **con el saldo bloqueado**, así dos cajas no
  venden la misma última unidad. Las ventas abiertas no reservan existencias. El kardex conserva `inventory.allow_negative_stock` = no.
- **Ajuste rápido** `POST /inventory/quick-adjustments` (permiso `inventory.adjustment.quick`, admite supervisor): entrada real al
  kardex de ese producto al costo promedio vigente, con motivo, publicada de inmediato y auditada; aparece en el reporte de ajustes
  para revisarla. No es un saldo negativo.
- **Costo de la línea = costo que asigna el kardex al completar** (promedio vigente; FEFO por lotes), guardado en la línea.
- **Lote vencido**: si el lote FEFO a descontar está vencido, `/lines` responde `SALES.EXPIRED_LOT_REQUIRES_AUTHORIZATION`; se agrega
  por `POST /sales/{id}/lines/expired` con `sales.expired.sell` (admite supervisor) y queda registrado en la línea. Prevención:
  `GET /inventory/lots?expiring=true` (lotes vencidos o por vencer en `inventory.expiry_alert_days`) para liquidarlos con una promoción.

## Consecuencias
- ✅ El kardex nunca queda negativo y los faltantes se ven de inmediato.
- ✅ La excepción (ajuste rápido) es trazable: quién, cuándo, por qué y a qué costo.
- ⚠️ En la futura caja autónoma (sin servidor) no hay saldo en tiempo real: se venderá contra el último saldo conocido y se conciliará.
