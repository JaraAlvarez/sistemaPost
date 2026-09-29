# ADR-0025 · Lotes solo con cantidades y salidas FEFO

- **Estado:** Aceptada · 2026-09-28 · Fase 5 · Decisiones D5-05 y D5-06 (pregunta 5)

## Contexto
Un supermercado controla lotes para los vencimientos (lácteos, carnes frías, medicamentos de venta libre), no para
costear. Costear por lote duplicaría el kardex valorizado y complicaría el costo promedio por bodega (ADR-0019).

## Decisión
- El **costo sigue siendo promedio por bodega**. La fila de `stock_balances` sin lote es el saldo valorizado del producto
  (Σ de todos sus movimientos, con o sin lote); cada lote tiene su fila de **solo cantidad** (valor 0, CHECK).
- Los movimientos de un producto con lotes llevan `lot_id`. Una salida sin lote indicado se reparte **FEFO** (primero el
  que vence antes) en un movimiento por lote; lo que no cubren los lotes sale sin lote (existencias previas al control).
- Un lote nunca queda negativo. El bloqueo de la fila del producto serializa también sus lotes.
- El número de lote es único por **sucursal** y producto (dos tiendas que reciben el mismo lote sin conexión no chocan).
- La verificación del kardex comprueba también que cada lote = Σ de sus movimientos. Alertas de vencimiento con
  `inventory.expiry_alert_days` (30).

## Consecuencias
- ✅ La caja no pregunta el lote; vence primero lo más viejo; costo estable.
- ⚠️ No hay utilidad por lote. Si la góndola no sigue FEFO, el sistema difiere del físico: se corrige con un ajuste por lote.
