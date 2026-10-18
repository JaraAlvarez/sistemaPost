# Notas de versión

## Rama `reportes-analisis` (pendiente de unir a `main`)

### Nuevo
- **Reportes → Análisis:** ABC de productos por venta y por utilidad, comparativos por día (año pasado = 364 días antes, mismo día de la
  semana), por categoría y por cajero (año pasado = mismas fechas), y mapa de calor de ventas por hora y día de la semana. El rango de un
  comparativo no puede pasar de la tercera parte de `reporting.max_range_days` (122 días con el valor por defecto).

### Cambia
- **Impuestos generados en ventas (`TAXES_SALES`):** la columna del valor se llama ahora **"Valor del impuesto"** (antes "Impuesto", el
  mismo nombre de la columna del código). En Excel, CSV y PDF el encabezado cambia; en el Excel antes se perdía una de las dos columnas.
  Quien tenga plantillas o macros que lean ese encabezado debe actualizarlas. La llave en JSON (`amount`) no cambia.

## Rama `pantallas-admin` (pendiente de unir a `main`)

### Nuevo
- **Configuración → Backups:** carpeta de los backups del servidor, último backup y su resultado, "Respaldar ahora" y destinos de disco
  externo o carpeta de red, con la carpeta comprobada en el servidor antes de guardar.
- **Facturación → Configuración → Factus:** conectar Factus desde la pantalla ("Conectar", "Volver a probar", "Cambiar llaves",
  "Desconectar"), documentos retenidos con "Enviar ahora" / "No enviar", rangos por ambiente.
- El adaptador por defecto es **FACTUS** (adenda A del ADR-0059, aprobada). La facturación nace apagada (OFF) y no envía nada sin una
  conexión verificada. Para prohibirla en una instalación: `"Pos": { "Billing": { "Provider": "NONE" } }` en `server.json`.

### Base de datos
- Migración **V2026.10.060** (`billing__provider_verification`): verificación de la conexión, atraso retenido, ambiente por documento y
  por rango de numeración, estado `NOT_SENT`.
- **Aviso para desarrollo:** la V060 se corrigió varias veces dentro de la rama y **cambió de checksum**. Una BD de desarrollo que ya
  aplicó una versión anterior de la V060 queda bloqueada por el migrador (script modificado). Recréala (`dev-db.ps1`) o borra su fila:
  `DELETE FROM system.schema_migrations WHERE script_name LIKE 'V2026.10.060__%';` y deshaz a mano lo que esa versión creó antes de migrar
  de nuevo. **En producción no hay ninguna**: la V060 no se ha desplegado.
- Al actualizar, una empresa con la facturación electrónica encendida sin verificación queda **apagada** y el tablero lo avisa.
