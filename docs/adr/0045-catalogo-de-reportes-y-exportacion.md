# ADR-0045 · Catálogo de reportes en el código, endpoint genérico y exportación auditada

- **Estado:** Aceptada · 2026-09-29 · Fase 9 · Decisiones D9-03, D9-09, D9-10, D9-11 y D9-12

## Contexto
Se necesitan ~30 reportes con los mismos comportamientos: parámetros validados, permisos, totales, paginación y exportación a Excel,
CSV y PDF. Un endpoint y un exportador por reporte multiplicarían el código y las diferencias entre reportes.

## Decisión
- **Catálogo en el código** (`ReportCatalog`): cada reporte declara código, nombre, grupo, descripción, **permiso**, parámetros
  (fecha, número o identificador, con valores por defecto como `today`), columnas (tipo, total por suma o por razón, marca de costo) y
  su SQL sobre las vistas. Agregar un reporte = agregar una definición.
- **Un endpoint genérico:** `GET /api/v1/reports` (catálogo visible según los permisos del usuario), `GET /api/v1/reports/{code}?…`
  (JSON paginado con los totales de TODAS las filas) y `…&format=xlsx|csv|pdf` (archivo). El permiso lo verifica el motor según el
  reporte; exportar exige además `reporting.report.export`.
- **Costos ocultos sin permiso (RN-REP-04):** las columnas marcadas como costo se quitan si el usuario no tiene `inventory.cost.view`
  ni `reporting.profit.view`. Los reportes de utilidad exigen `reporting.profit.view`.
- **Exportación:** Excel con **MiniExcel** (Apache-2.0; hoja *Reporte* y hoja *Información* con empresa, parámetros y fecha), CSV
  nativo (separador `;`, UTF-8 con BOM y coma decimal para Excel en español) y PDF con **PDFsharp-MigraDoc** (MIT): tabla sencilla
  con el encabezado de la empresa, parámetros, fecha de generación, totales y número de página. QuestPDF se descartó por su licencia.
- **Cada exportación queda en la auditoría** (`REPORT_EXPORTED`: reporte, formato, parámetros, filas; severidad *Warning* si incluye
  costos o es antifraude). Por eso la exportación es un comando (se guarda en la transacción del caso de uso).
- **Antifraude** (D9-11): vista `reporting.antifraud_events` que une líneas eliminadas, cancelaciones, anulaciones, descuentos manuales,
  precios cambiados, cajón sin venta, lotes vencidos vendidos, ajustes rápidos y cierres con diferencia; el reporte por cajero calcula
  eventos por cada 100 tiquetes y resalta a quien supera el promedio de la tienda por el factor ⚙️
  `reporting.antifraud_threshold_factor` (2). Sin notificaciones en esta fase.
- **Permisos** (D9-10): el código `reporting.export` de la propuesta se implementó como `reporting.report.export` porque el formato de
  permisos exige `modulo.recurso.accion`.

## Consecuencias
- Todos los reportes se exportan, paginan, totalizan y protegen igual.
- Un reporte con necesidades muy particulares (gráficos, varias tablas) no cabe en el modelo y tendría su propio endpoint.
- En Linux (la nube) el PDF usa DejaVu Sans o Liberation Sans instaladas; en Windows, las fuentes del sistema.
