# 02 · Módulos y funcionalidades (B, C)

> Estado: **PROPUESTA — pendiente de aprobación**

La columna **Plan** indica el plan mínimo sugerido (B = Básico, P = Profesional, E = Empresarial). Es una propuesta comercial; se configura en el servidor de licencias, no en el código.

## B. Lista de módulos

| # | Módulo (código) | Responsabilidad | Plan |
|---|---|---|---|
| 1 | `Organization` | Empresa, sucursales, bodegas, cajas/terminales, datos fiscales, configuración | B |
| 2 | `Identity` | Autenticación, usuarios, empleados, roles, permisos, sesiones, autorizaciones de supervisor | B |
| 3 | `Catalog` | Productos, categorías, marcas, unidades, códigos de barras, presentaciones, precios, impuestos | B |
| 4 | `Inventory` | Saldos, kardex, ajustes, conteos físicos, traslados, lotes/vencimientos | B (avanzado P/E) |
| 5 | `Purchasing` | Proveedores, órdenes de compra, compras/recepciones, cuentas por pagar, devoluciones a proveedor | B (OC y CxP: P) |
| 6 | `Sales` (POS) | Venta en curso, líneas, descuentos, pagos, suspender/recuperar, finalizar, anular | B |
| 7 | `Returns` | Devoluciones de clientes (dentro de `Sales` como submódulo) | B |
| 8 | `Cash` | Cajas, jornadas, movimientos de efectivo, arqueos, cierres | B |
| 9 | `Parties` | Terceros: clientes y proveedores (identificación única), contactos | B |
| 10 | `Customers` | Clientes, historial; preparado para crédito, CxC, puntos | B (crédito/puntos: P/E) |
| 11 | `Expenses` | Gastos y categorías de gasto | B |
| 12 | `Billing` | Documentos fiscales, numeración autorizada, adaptadores de facturación electrónica | B |
| 13 | `Reporting` | Consultas y reportes, exportación | B (avanzados P) |
| 14 | `Audit` | Bitácora de auditoría inmutable, consultas | B |
| 15 | `Settings` | Configuración parametrizable por empresa/sucursal/caja | B |
| 16 | `Licensing` (cliente) | Estado de licencia, entitlements, activación, heartbeat | B |
| 17 | `Backup` | Backups manuales/automáticos, restauración, historial | B (nube: P/E) |
| 18 | `Devices` (Terminal Agent) | Impresoras, cajón, báscula, pantalla cliente, escáner | B |
| 19 | `Updates` | Verificación, descarga e instalación de actualizaciones | B |
| 20 | `Sync` *(futuro)* | Sincronización tienda ↔ nube / sucursales | E |
| — | `SharedKernel` | Tipos base: dinero, cantidades, IDs, resultados, reloj, eventos | transversal |

Módulos **transversales** (no son funcionalidades de negocio pero todos los usan): `SharedKernel`, `Audit`, `Settings`, `Licensing` (feature gates), autorización (`Identity`).

## C. Funcionalidades por módulo

### 1. Organization
- Empresa: razón social, nombre comercial, NIT/identificación + DV, régimen tributario, responsabilidades fiscales, dirección, logo, moneda, zona horaria.
- Sucursales: código, dirección, teléfono, bodega por defecto.
- Bodegas (almacenes): por sucursal; tipos: venta (piso), depósito, averías/cuarentena.
- Cajas/terminales: código, sucursal, bodega de despacho, serie de documentos, periféricos asignados, dispositivo físico vinculado.
- Configuración de documentos: encabezados/pies de tiquete, textos legales, copias.
- Resoluciones/rangos de numeración fiscal (se gestionan en `Billing`).

### 2. Identity
- Login con usuario + contraseña; **login rápido de cajero con PIN** en caja registrada.
- Usuarios: estado, bloqueo por intentos fallidos, cambio obligatorio de contraseña, expiración opcional.
- Empleados: datos de la persona, cargo, fecha de ingreso; un empleado puede tener o no usuario.
- Roles con permisos; roles con **alcance por sucursal**; excepciones por usuario (conceder/denegar).
- Sesiones: token, dispositivo, IP, expiración, cierre remoto, cierre por inactividad.
- **Autorización de supervisor** (override): el cajero pide; el supervisor ingresa PIN; queda registrado quién autorizó qué.
- Políticas: longitud de contraseña, intentos, tiempo de inactividad.

### 3. Catalog
- Productos: SKU interno, nombre, nombre corto (tiquete), descripción, categoría/subcategoría (árbol), marca, unidad base, tipo de venta (unidad / peso / volumen), maneja inventario (sí/no — ej. servicios, recargas), maneja lotes/vencimiento, se vende por báscula, permite precio abierto, permite decimales, estado.
- Categorías jerárquicas ilimitadas (categoría → subcategoría → ...).
- Marcas, unidades de medida (UND, KG, G, L, ML, etc.) con conversiones.
- Códigos de barras múltiples por producto y por presentación; código principal.
- **Presentaciones** (empaques): factor respecto de la unidad base, código de barras propio, precio propio opcional.
- **Reglas de códigos de peso/precio variable** (prefijo, posiciones de PLU, peso o precio, decimales).
- Listas de precios (general, mayorista, etc.) con vigencia; precio con/sin impuesto; historial de precios.
- Costos: último costo, costo promedio (derivado de inventario), costo estándar opcional.
- Impuestos por producto (uno o varios).
- Stock mínimo / máximo / punto de pedido por bodega.
- Importación masiva desde Excel/CSV con validación.
- Productos relacionados a proveedores.

### 4. Inventory
- Saldos por producto/bodega (y por lote si aplica).
- **Kardex** completo con filtros; costo promedio ponderado.
- Ajustes (positivos/negativos) con motivo obligatorio: pérdida, daño, vencimiento, robo, consumo interno, corrección.
- Conteos físicos: total, parcial (por categoría/ubicación), cíclico; conteo ciego; reconteo; aprobación; generación automática de ajustes.
- Traslados entre bodegas y sucursales: envío → en tránsito → recepción (con diferencias).
- Lotes y vencimientos (P/E): FEFO, alertas.
- Alertas de stock bajo mínimo; sugerido de compra.
- Valorización del inventario a una fecha.

### 5. Purchasing
- Proveedores (sobre `Parties`), contactos, condiciones de pago, productos que suministra con código del proveedor y último costo.
- Órdenes de compra: borrador → enviada → recibida parcial/total → cerrada/cancelada.
- Compras/recepciones (factura del proveedor): contra OC o directas; costos, descuentos, impuestos, fletes; actualización de inventario y costo promedio al **contabilizar**.
- Formas de pago: contado (opcionalmente desde caja) o crédito → cuenta por pagar.
- Cuentas por pagar: saldos, vencimientos, abonos, pagos parciales.
- Devoluciones a proveedor: contra compra; salida de inventario; nota débito / crédito a favor; afecta CxP.
- Historial de compras por proveedor/producto; variación de costos.

### 6. Sales / POS
- Venta en curso persistida; varias ventas suspendidas por caja.
- Escaneo (incluye códigos de peso variable), búsqueda, agregar por PLU.
- Cambiar cantidad, lectura de báscula, precio abierto (si el producto lo permite y con permiso).
- Descuentos por línea y globales (% o valor), con límite por rol y autorización.
- Eliminar línea antes de pagar (queda registro de línea anulada).
- Cliente: consumidor final por defecto o identificado.
- Pagos múltiples, cambio, referencia de transferencia/voucher.
- Finalizar venta (transacción atómica: venta + inventario + caja + documento fiscal pendiente).
- Reimpresión de tiquete (auditado).
- Anulación de venta completada (permiso + motivo + nota crédito si ya es documento fiscal).
- Consulta de precio sin vender.

### 7. Returns (clientes)
- Buscar venta por número/código de tiquete/fecha/cliente.
- Seleccionar líneas y cantidades (no más de lo vendido menos lo ya devuelto).
- Motivo obligatorio; destino de la mercancía: vuelve a stock vendible o a bodega de averías.
- Reintegro: efectivo (desde caja abierta), mismo medio, saldo a favor (futuro).
- Devolución sin tiquete: solo con permiso especial (configurable).
- Genera nota crédito fiscal cuando corresponda.

### 8. Cash
- Cajas (registros) y jornadas (sesiones de caja).
- Apertura con fondo inicial (y conteo por denominaciones opcional).
- Ingresos manuales, retiros (sangrías) con autorización, pagos de gastos, reintegros por devolución.
- Apertura de cajón sin venta (registrada).
- Cierre: conteo por denominación y por medio de pago; arqueo ciego opcional; diferencias (sobrante/faltante); aprobación del supervisor.
- Historial de cierres, reimpresión de reporte Z/X.
- Reporte X (parcial, sin cerrar) y Z (cierre).

### 9–10. Parties / Customers
- Terceros: tipo de persona, tipo y número de identificación, DV, nombres/razón social, régimen, responsabilidades fiscales, correo (para factura electrónica), teléfono, dirección, ciudad.
- Clientes: estado, grupo, lista de precios asignada, historial de compras, total comprado.
- Preparado (tablas futuras): cupo de crédito, cuentas por cobrar, puntos y fidelización.

### 11. Expenses
- Categorías de gasto; registro de gasto con soporte, proveedor opcional, forma de pago; si se paga en efectivo desde caja genera movimiento de caja.

### 12. Billing
- Separación: **venta** (operación comercial) ≠ **documento fiscal** (representación legal).
- Tipos: tiquete interno, documento equivalente POS electrónico, factura electrónica, nota crédito, nota débito.
- Rangos/resoluciones de numeración autorizados con vigencia y alerta de agotamiento.
- Estado fiscal independiente del estado de la venta; cola de envío asíncrona con reintentos.
- Registro de cada intercambio con el proveedor (request/response, códigos, CUFE/CUDE, XML, QR).
- Modo contingencia.
- Adaptadores intercambiables por proveedor tecnológico.

### 13. Reporting
- Ventas del día/rango, por usuario, caja, sucursal, producto, categoría, marca, medio de pago, hora.
- Más/menos vendidos, sin rotación, utilidad bruta y margen, impuestos generados/descontables.
- Inventario valorizado, kardex, stock bajo mínimo, vencimientos.
- Compras, devoluciones, gastos, CxP.
- Caja: cierres, diferencias, retiros; **antifraude**: anulaciones, cancelaciones, descuentos, aperturas de cajón.
- Exportación PDF/Excel/CSV; reportes gateados por plan.

> **Implementado en la Fase 9** (ADR-0044 a 0046): módulo de solo lectura sobre las vistas del esquema `reporting` (contrato estable),
> catálogo de 33 reportes en el código con un endpoint genérico `GET /api/v1/reports/{code}` (JSON paginado o archivo xlsx/csv/pdf
> auditado) y tablero del día `GET /api/v1/reports/dashboard`. **Todos los reportes están en ambas ediciones** (ADR-0015): la línea
> "reportes gateados por plan" quedó reemplazada. Cada tienda ve los datos de su nodo; la consolidación de sucursales llega con la nube.

### 14. Audit
- Registro automático de cambios en entidades maestras (antes/después) y eventos de negocio sensibles.
- Consulta por usuario, módulo, entidad, rango de fechas; historial de un registro.
- Inmutable y con evidencia de manipulación (hash encadenado).

### 15. Settings
- Configuración tipada por alcance (empresa → sucursal → caja), con valores por defecto. Ej.: permitir stock negativo, redondeo, arqueo ciego, días de gracia de devolución, límite de descuento del cajero, tiempo de inactividad.

### 16. Licensing (cliente local)
- Activación con clave, estado actual, plan, módulos y límites, fecha de vencimiento, días de gracia.
- Heartbeat periódico, renovación del token, alertas de vencimiento.
- Guardas de funcionalidad (feature gates) y límites (nº de cajas, sucursales).

### 17. Backup
- Manual y programado; al cierre de jornada; antes de cada actualización.
- Destinos múltiples, cifrado, retención, verificación, restauración guiada, historial.

### 18. Devices (Terminal Agent)
- Impresora térmica ESC/POS (USB, serial, red, spooler Windows), impresora convencional (A4/PDF).
- Cajón monedero (pulso por impresora o serial).
- Báscula (serial/USB, protocolos por marca).
- Lector de códigos (teclado/HID, serial).
- Pantalla secundaria (segundo monitor o visor VFD).
- Cola de impresión con reintentos; pruebas de periféricos.

### 19. Updates
- Consulta de versión disponible, descarga firmada, instalación programada fuera de jornada, migraciones, rollback.
