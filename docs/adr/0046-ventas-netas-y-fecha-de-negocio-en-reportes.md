# ADR-0046 · Ventas netas, utilidad y fecha de negocio en los reportes

- **Estado:** Aceptada · 2026-09-29 · Fase 9 · Decisiones D9-04, D9-05, D9-06 y D9-07

## Contexto
Los reportes deben cuadrar con el reporte Z, con el historial del cliente y entre sí. Una venta de las 00:30 pertenece al turno que la
hizo (ADR-0028); un cambio de mercancía no puede contarse dos veces; la utilidad no puede recalcularse con los costos de hoy.

## Decisión
- **Fecha de negocio** (`business_date` de la jornada) para todo lo de ventas y caja; la distribución **por hora** usa la hora real del
  cobro en hora de Colombia. Los reportes lo explican en su nota.
- **Ventas netas** = total de las ventas **completadas** − créditos de **cambios** − **reintegros por garantía** completados en el
  período. Las ventas anuladas no suman: se muestran aparte (cantidad y valor). El ajuste de redondeo del efectivo se muestra aparte.
- **Utilidad** = venta sin impuestos (`tax_base` de las líneas activas) − costo guardado en cada línea al cobrar (`cost_total`, costo
  promedio del kardex de ese momento), **neto de lo devuelto** en el período (base proporcional y costo de las líneas devueltas).
  Margen = utilidad ÷ venta sin impuestos × 100. En el reporte por cajero la devolución resta al cajero de la venta original.
- **Inventario a una fecha** = saldo (cantidad y valor) del último movimiento del kardex con fecha de negocio ≤ la fecha, por bodega y
  producto. Hoy se leen los saldos en línea. Ambos cuadran porque el kardex guarda el saldo resultante de cada movimiento (ADR-0019).
- **Igualdades de cuadre** verificadas por la prueba de la fase: Σ por medio de pago = total vendido; Σ por producto = ventas netas del
  día; Σ impuestos por tarifa = impuestos de las ventas; utilidad = venta neta − costo; valorizado a hoy = valorizado a la fecha de hoy.
- **Libro de ventas diario** con el formato estándar (por día y caja: consecutivos, base e IVA por tarifa, excluidos/exentos,
  impuesto al consumo, total y medios), pendiente de validar con el contador.
- **Datos personales (RN-REP-05):** el detalle por cliente solo muestra a los clientes cuya última decisión de autorización SERVICE es
  "otorgada"; los demás se agrupan en una fila.

## Consecuencias
- Los reportes pueden diferir de un día respecto a la hora del reloj (se explica en la nota del reporte).
- El reporte de impuestos de ventas no resta devoluciones (se registran por separado en la contabilidad).
