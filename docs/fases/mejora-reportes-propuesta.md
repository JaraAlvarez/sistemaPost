# Mejora: reportes de análisis (ABC/Pareto, comparativos y mapa de calor) — propuesta

Estado: **aprobada e implementada** en la rama `reportes-analisis` (desde main 8a1cc3c). La propuesta se escribió sobre 4fc24ce; los cambios respecto al diseño están en la §7.

## 1. Qué se agrega

Un grupo nuevo **"Análisis"** en Administración → Reportes con 6 reportes:

| Código | Qué muestra | Permiso (existente) |
|---|---|---|
| `ABC_BY_SALES` | Productos clasificados A/B/C por venta neta, con participación y % acumulado | `reporting.sales.advanced` |
| `ABC_BY_PROFIT` | Lo mismo, por utilidad (base sin impuestos − costo) | `reporting.profit.view` |
| `COMPARE_BY_DAY` | Cada día del rango contra el periodo anterior y el mismo periodo del año pasado | `reporting.sales.basic` |
| `COMPARE_BY_CATEGORY` | El comparativo agrupado por categoría | `reporting.sales.advanced` |
| `COMPARE_BY_CASHIER` | El comparativo agrupado por cajero | `reporting.sales.basic` |
| `SALES_HEATMAP` | Ventas por hora (filas) × día de la semana (columnas) | `reporting.sales.basic` |

Todos se exportan a Excel, CSV y PDF con el exportador actual. **No se crean permisos, migraciones, vistas SQL ni códigos de auditoría.** Por eso no se tocan las pruebas de conformidad de permisos ni R9 y R10.

## 2. Reglas de cálculo

- **ABC:** los cortes son configurables y van por defecto en A = 80 % y B = 15 %; C es el resto.
  - Un producto es A si el acumulado *antes* de él es menor que el corte A. Así, el que cruza el 80 % queda en A.
  - Un producto con valor ≤ 0 va a C con participación 0.
  - El total que sirve de base se calcula en SQL con una función de ventana. Así sigue siendo correcto aunque el reporte se corte por el límite de filas.
- **Comparativo:** compara ventas netas, tiquetes, ticket promedio y margen. Para cada uno muestra el valor actual, el anterior y su variación %, y el del año pasado y su variación %. En el margen, la variación va en puntos porcentuales.
  - *Periodo anterior* = los mismos N días justo antes.
  - *Año pasado* = las mismas fechas con un año menos (el 29 de febrero pasa al 28).
  - Con valor anterior en 0, la variación queda vacía.
- **Mapa de calor:** la hora sale de la hora real del cobro (America/Bogota) y el día de la semana, de la fecha de negocio, igual que el reporte "Ventas por hora".
- Se siguen las reglas actuales:
  - venta neta = completadas − cambios − reintegros;
  - las anuladas no cuentan;
  - las devoluciones restan en su fecha;
  - el costo es el promedio del kardex guardado en la línea;
  - las columnas de costo se ocultan sin permiso.

## 3. Cambios técnicos

Todos los cambios quedan dentro de `src/Modules/Reporting` y de `Reportes.razor`.

- **`ReportModel.cs`:**
  - columnas `Hidden` (se leen del SQL pero no se publican), `Computed` (las llena C#) y `Variation`;
  - una interfaz opcional `IReportCalculation` (`Prepare` y `Apply`) en `ReportDefinition`.
  - Son parámetros opcionales al final, así que no se rompen las 40 definiciones actuales.
- **`ReportUseCases.cs`:** el motor llama a `Prepare` y `Apply`, y calcula los totales sobre todas las columnas antes de quedarse con las visibles.
- **`ReportAnalytics.cs` (nuevo):** contiene la lógica pura y probable sin base de datos: `AbcClassifier`, `ComparisonPeriods` y `PeriodComparison`.
- **`ReportCatalog.cs`:** el grupo `Análisis` y las 6 definiciones.
  - El comparativo usa un solo recorrido, que une las ventas a los 3 periodos con `VALUES` y `BETWEEN`, para aprovechar el índice `(branch_id, business_date)`.
- **`ReportingInfrastructure.cs`:** no lee del SQL las columnas `Computed`.
- **`Reportes.razor`:**
  - clase ABC con color (A verde, B ámbar, C gris);
  - variaciones con ▲ verde / ▼ rojo;
  - interruptor "Tabla / Mapa de calor", con celdas más oscuras cuanto mayor la venta;
  - los valores por defecto `today-N` ya se muestran en el selector de fecha.

## 4. Pruebas

- **Unitarias nuevas** en `tests/Pos.Modules.Reporting.UnitTests`:
  - el clasificador ABC (bordes en 80 %, un solo producto, ceros y negativos, cortes inválidos);
  - los periodos (29 de febrero, cruce de año);
  - las variaciones;
  - el motor con un ejecutor falso (columnas ocultas, totales, costos sin permiso);
  - invariantes del catálogo.
- **Integración** `Phase9/AnalyticsReportsTests`:
  - ABC cuadra con "Ventas por producto";
  - el comparativo refleja ventas movidas al periodo anterior y al año pasado;
  - el mapa cuadra con "Ventas por hora";
  - 403 en `ABC_BY_PROFIT` sin `reporting.profit.view`;
  - exportación a xlsx y pdf.
- **Pruebas existentes:** `ReportsApiTests` ejecuta automáticamente cada reporte del catálogo. También se corren ArchitectureTests, `ConformityTests`, `ReportingSchemaTests` y el build en Release.

## 5. Decisiones (aprobadas por el usuario el 2026-10-17: las recomendadas)

1. **Proyecto de pruebas nuevo:** las pruebas unitarias exigen crear `tests/Pos.Modules.Reporting.UnitTests` y agregarlo a `Pos.slnx`, que está fuera de los archivos de Reporting. ¿De acuerdo?
2. **"Año pasado":** hay dos opciones:
   - mismas fechas (1–31 oct 2025): es lo más fácil de entender, pero los días de la semana no coinciden;
   - 364 días antes: lunes contra lunes, mejor para supermercados.
   Recomiendo **364 días** en el comparativo por día y **mismas fechas** en categoría y cajero, o una sola regla si prefiere simplicidad.
3. **PDF ancho:** el comparativo tiene unas 21 columnas y en PDF horizontal queda apretado. Opciones:
   - reducir la letra cuando hay más de 15 columnas;
   - aceptarlo y recomendar Excel.
   Recomiendo reducir la letra.

## 6. Riesgos

- **Rendimiento:** el comparativo lee hasta 3 veces el rango, y `return_lines` no tiene índice por fecha. Hay que medir con unos 100.000 tiquetes contra el límite de 30 s. Si no alcanza, haría falta un índice nuevo en una tabla de Ventas, que se propondría aparte.
- **Líneas sin costo:** como el costo vale 0 cuando falta, el margen queda inflado. Se indicará en la nota del reporte.
- **Fecha del cliente y del servidor:** cerca de la medianoche pueden diferir.

## 7. Ajustes al implementar

Se revisó el módulo Reporting de main (8a1cc3c) antes de escribir código. Diferencias con el diseño de las §1–§4:

- **Sin migraciones, vistas nuevas ni permisos nuevos.** Las vistas actuales (`reporting.sales`, `sale_lines`, `returns`, `return_lines`, `products`, `categories`, `users`) ya tienen todo lo necesario. Por eso no hizo falta la V2026.10.120 ni tocar `R__reporting__views` o `A__system__privileges`: el `GRANT SELECT ON ALL TABLES IN SCHEMA reporting TO pos_app` cubriría cualquier vista futura. `ReportingSchemaTests` sigue esperando 37 vistas.
- **Alcance por sucursal.** En main, los reportes no tienen filtro de sucursal. Siempre leen la empresa y la sucursal del nodo (`@company_id`, `@branch_id`, D9-13), y el permiso se verifica con el alcance del usuario en esa sucursal. Los reportes nuevos siguen la misma regla. No se agregó ningún parámetro de sucursal, así que no es posible consultar otra sucursal.
- **Presentación de las columnas.** En lugar de una marca `Variation` en la columna, se agregó `ReportColumnStyle` (`None`, `Variation`, `AbcClass`, `Heat`) en los contratos. `ReportColumnDto` lo publica como parámetro opcional al final, y la pantalla lo usa para ▲/▼, el color de la clase y el mapa de calor. `HeatmapScale` (intensidad de 0 a 5) también quedó en los contratos, porque el cliente ya los referencia.
- **`IReportCalculation`** tiene tres pasos: `Prepare` (valida, agrega argumentos SQL y parámetros que se muestran), `Apply` (llena las columnas `Computed` de cada fila) y `CompleteTotals` (rehace las variaciones, promedios y márgenes del total sobre las sumas, no suma variaciones). El motor calcula los totales con todas las columnas (incluidas las ocultas) y luego proyecta las visibles. Para los 39 reportes que ya existían, el resultado es el mismo (ahora son 45).
- **Cortes del ABC** como parámetros del reporte (`cutA` = 80, `cutB` = 15). Con valores inválidos (A fuera de 1–99, o A + B > 100) responde `REPORTING.INVALID_PARAMETER`.
- **Periodos mostrados.** El comparativo agrega a los parámetros del resultado y de los archivos el "Periodo anterior" y el "Año pasado", con sus fechas.
- **Columnas del comparativo:** 20 visibles (venta neta, tiquetes, ticket promedio y margen; cada uno con actual, anterior, var., año pasado, var.) más la clave, y 9 ocultas (total, base y costo por periodo). Ticket promedio = total vendido ÷ tiquetes, igual que en "Resumen diario". Las 5 columnas de margen solo se ven con `inventory.cost.view` o `reporting.profit.view`.
  - En el comparativo por categoría, un tiquete con varias categorías cuenta en cada una. Por eso ahí no se totalizan los tiquetes ni el ticket promedio.
  - En el comparativo por cajero, las devoluciones restan al cajero de la venta original, como en "Utilidad por cajero".
- **Valores por defecto:** últimos 7 días en los comparativos, 30 en el ABC y 28 en el mapa de calor. Ahora la pantalla resuelve `today-N` en el selector de fecha.
- **PDF:** la letra baja a 5 pt con más de 15 columnas. Con 13 a 15 se mantiene en 6 pt, como ya estaba.
- **Mapa de calor:** se muestra la venta con impuestos de las ventas completadas, como en "Ventas por hora". Las filas son solo las horas con ventas. En la pantalla se puede elegir "Tabla" o "Mapa de calor".
- **Corrección encontrada por las pruebas:** en `TAXES_SALES` dos columnas se llamaban "Impuesto". En Excel, la segunda pisaba a la primera. La del valor ahora se llama "Valor del impuesto". Una prueba del catálogo exige etiquetas únicas.
- **Pruebas:**
  - `tests/Pos.Modules.Reporting.UnitTests` (en `Pos.slnx`): ABC, periodos (364 días, 29 de febrero, cruce de año), variaciones, mapa de calor, motor con un ejecutor falso, invariantes del catálogo, y PDF, Excel y CSV del comparativo. Para esto, `Pos.Modules.Reporting.Infrastructure` agrega `InternalsVisibleTo` a ese proyecto.
  - Integración en `Phase9/AnalyticsReportsTests`.
- **Pendiente (§6):** no se midió el rendimiento con unos 100.000 tiquetes.
