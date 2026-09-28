# 01 · Análisis del producto (A) e investigación previa

> Estado: **PROPUESTA — pendiente de aprobación** · Fase 1 · Versión 0.1

## A. Análisis general del producto

### Qué estamos construyendo

Una **plataforma POS comercial para supermercados y comercio minorista**, instalable en Windows y que funciona **primero en local**, vendida por **suscripción mensual** con planes que habilitan módulos y límites (cajas, sucursales, funciones).

No es una "app de facturación": es un **sistema transaccional de misión crítica**. Si el POS se detiene, el supermercado deja de vender. Eso define todas las prioridades:

| Prioridad | Significado práctico |
|---|---|
| 1. Continuidad de ventas | Ninguna causa externa (Internet, servidor de licencias, proveedor de facturación) puede detener una venta en curso ni una caja abierta. |
| 2. Integridad de datos | El dinero y el inventario deben cuadrar siempre. Nada se "edita a mano": todo es un documento o un movimiento trazable. |
| 3. Trazabilidad | Quién, cuándo, desde qué caja, con qué autorización y qué valor cambió. |
| 4. Velocidad en caja | Escanear → agregar línea en < 100 ms en LAN. El cajero no espera al sistema. |
| 5. Operación sin técnicos | Instalar, actualizar, respaldar y restaurar sin conocimientos técnicos. |
| 6. Crecimiento | Una caja → varias cajas → varias sucursales → nube, sin rehacer el modelo. |

### Perfil de clientes objetivo

| Segmento | Cajas | Sucursales | Necesidad dominante |
|---|---|---|---|
| Minimercado / tienda de barrio | 1 | 1 | Simplicidad, precio, rapidez, un solo PC |
| Supermercado mediano | 2–8 | 1 | Varias cajas en red local, control de cajeros, compras, inventario serio |
| Cadena pequeña | 5–40 | 2–10 | Traslados, precios centralizados, consolidación de reportes |

### Usuarios del sistema

- **Cajero**: vende, cobra, abre/cierra su caja. Necesita velocidad y pocas decisiones.
- **Supervisor de cajas**: autoriza anulaciones, descuentos, devoluciones, retiros; revisa cierres.
- **Encargado de inventario/bodega**: recibe mercancía, conteos, ajustes, traslados.
- **Comprador**: órdenes de compra, proveedores, costos.
- **Administrador / dueño**: precios, usuarios, configuración, reportes, utilidad.
- **Contador**: reportes fiscales, impuestos, cuentas por pagar (solo lectura mayormente).
- **Soporte técnico (nuestro)**: instalación, licencias, diagnóstico (con acceso controlado y auditado).

---

## Investigación: funcionalidades de un POS profesional de supermercado

Referencia: funcionalidades comunes en sistemas de retail de supermercado (tipo LS Retail/LS Central, NCR, Toshiba, Oracle Retail, y POS regionales latinoamericanos), más buenas prácticas de software empresarial.

### 1. Imprescindibles (MVP comercial — sin esto no se puede vender el producto)

- Venta rápida por **escáner de código de barras** y búsqueda por nombre/código/SKU.
- Productos **por unidad, por peso y por volumen**; cantidades decimales.
- **Códigos de barras de peso/precio variable** (EAN-13 con prefijo 2x generado por la báscula etiquetadora). Clave en supermercados (carnes, frutas, fiambres).
- Múltiples códigos de barras por producto y **presentaciones** (unidad, paquete x6, caja x24) con su propio código y precio.
- Impuestos por producto (IVA con varias tarifas, exento/excluido, impuestos al consumo) y **precios con impuesto incluido**.
- Descuentos por línea y por venta, con límites por rol.
- **Pagos combinados** (efectivo + tarjeta + transferencia) y cálculo de cambio.
- **Suspender / recuperar venta**.
- **Caja**: apertura con fondo, retiros (sangrías), ingresos, gastos, cierre con arqueo y diferencias.
- **Inventario con kardex** (movimientos, nunca edición directa de stock) y costo promedio.
- Compras / recepción de mercancía que actualiza inventario y costos.
- Devoluciones de clientes con reintegro y efecto en inventario.
- Usuarios, roles, permisos y **autorización de supervisor** (PIN/clave) para acciones sensibles.
- Auditoría de acciones sensibles.
- Impresión de tiquete en **impresora térmica ESC/POS** y apertura de **cajón monedero**.
- Numeración de documentos consecutiva y sin duplicados.
- **Documento fiscal** según normativa del país (en Colombia, el tiquete POS debe ser *documento equivalente electrónico* — ver riesgos en doc 12).
- Backups automáticos y restauración.
- Reportes básicos: ventas del día, por medio de pago, cierre de caja, inventario valorizado.
- Funcionamiento **100 % sin Internet**.

### 2. Importantes (diferencian un producto profesional)

- **Varias cajas en red local** contra un servidor de tienda.
- Listas de precios, precios programados (vigencia desde/hasta), cambio masivo de precios.
- **Báscula conectada** a la caja (lectura directa del peso).
- Pantalla secundaria para el cliente.
- Órdenes de compra, recepción parcial, cuentas por pagar, devoluciones a proveedor.
- Conteos físicos (inventario total, parcial y cíclico) con conteo ciego.
- Stock mínimo/máximo y sugerido de compra.
- Gastos con categorías.
- Clientes con historial; cliente genérico "consumidor final".
- Reportes: utilidad, más/menos vendidos, por cajero, por categoría, impuestos, kardex.
- Reporte antifraude: líneas anuladas, ventas canceladas, aperturas de cajón sin venta, descuentos.
- Arqueo **ciego** (el cajero cuenta sin ver el valor esperado).
- Etiquetas de góndola / cambios de precio pendientes de etiquetar.
- Exportación a Excel/PDF.

### 3. Avanzadas (planes Profesional / Empresarial)

- Multisucursal, traslados entre bodegas/sucursales con estado "en tránsito".
- Precios y catálogo centralizados, consolidación de reportes.
- **Lotes y fechas de vencimiento** (FEFO), alertas de vencimiento.
- Promociones: 2x1, lleve 3 pague 2, % por categoría, combos, happy hour, precio por volumen.
- Crédito a clientes / cuentas por cobrar.
- Puntos y fidelización.
- Integración con datáfonos (pago integrado con respuesta automática).
- Productos compuestos (kits/canastas) y producción simple (porcionado de carnes: 1 res → cortes).
- Pedidos a proveedor automáticos por stock mínimo.
- Roles con permisos por sucursal.
- Modo offline de caja ante caída del servidor de tienda (LAN).

### 4. Futuras (roadmap de producto)

- Consola en la nube (dashboard del dueño desde el celular).
- Sincronización tienda ↔ nube y **multisucursal en tiempo real**.
- Tienda en línea / pedidos a domicilio / integración con apps de delivery.
- Autopago (self-checkout) y consulta de precios por quiosco.
- Terminal móvil para inventario (Android con escáner).
- Integración contable (exportación a software contable).
- Etiquetas electrónicas de góndola (ESL).
- Analítica y predicción de demanda.
- API pública para integraciones de terceros.
- Marketplace de módulos / add-ons de pago.

---

## Problemas habituales de los POS y cómo los evitamos desde la arquitectura

| # | Problema típico | Causa raíz | Solución arquitectónica |
|---|---|---|---|
| 1 | El stock "no cuadra" y nadie sabe por qué | El stock es un campo editable | **Kardex append-only**: el stock solo cambia por movimientos con documento origen. El saldo es un derivado. |
| 2 | Al cambiar el precio o nombre de un producto, las facturas viejas cambian | Las líneas referencian al producto vivo | Las líneas de documentos guardan **snapshot** (nombre, código, precio, impuestos, costo). |
| 3 | Errores de centavos, totales que no cuadran con los impuestos | Uso de `float/double` | Tipo `decimal` en código y `numeric` en BD. Reglas de redondeo explícitas y centralizadas. |
| 4 | Números de factura duplicados o saltados | Numeración calculada con `MAX()+1` | Secuencias por documento/caja con bloqueo transaccional; anulación nunca libera número. |
| 5 | Venta perdida por corte de luz o cierre de la app | Carrito solo en memoria | Venta en curso **persistida** en BD (estado `OPEN`); se recupera al reiniciar. |
| 6 | Ventas duplicadas por doble clic o reintentos de red | Operaciones no idempotentes | **Claves de idempotencia** en operaciones críticas (finalizar venta, pagos). |
| 7 | Fraude interno (anular líneas, abrir cajón, descuentos no autorizados) | Falta de permisos finos y registro | Permisos granulares, **autorización de supervisor registrada**, auditoría y reporte antifraude. |
| 8 | Caja cerrada sin trazabilidad | Ventas no vinculadas a la jornada | Toda venta y movimiento de dinero exige `cash_session_id` abierta. |
| 9 | Internet se cae y no se puede vender | Arquitectura cloud-first | **Local-first**: todo funciona en la LAN; Internet solo para licencia, facturación electrónica, actualizaciones. |
| 10 | La licencia bloquea el negocio en plena jornada | Verificación online obligatoria | Licencia **firmada y verificable offline** con periodo de gracia; nunca se corta una caja abierta. |
| 11 | Actualización que corrompe la base de datos | Migraciones manuales | Migraciones versionadas + **backup automático previo** + rollback. |
| 12 | Pérdida total de datos al dañarse el disco | Sin backups o backups nunca probados | Backups automáticos cifrados, en ubicaciones múltiples, **con verificación**. |
| 13 | Lentitud con 30.000+ productos | Búsquedas `LIKE '%x%'` sin índices | Índices por código de barras, trigramas para texto, consultas medidas. |
| 14 | Stock negativo por dos cajas vendiendo lo mismo | Condiciones de carrera | Transacciones ACID y bloqueo de fila sobre el saldo. |
| 15 | Reportes por fecha incorrectos (ventas de las 00:30 en otro día) | Mezclar fecha/hora con jornada | Timestamps en **UTC** + `business_date` (fecha de jornada) separada. |
| 16 | Hardware amarrado al código (cambiar impresora = reprogramar) | Llamadas directas a drivers | **Capa de abstracción de periféricos** con drivers intercambiables configurables. |
| 17 | "Borrar" un producto rompe historial | Borrado físico | Borrado lógico / inactivación; FK restrictivas. |
| 18 | Integración de facturación electrónica obliga a rehacer ventas | Venta y factura mezcladas | **Venta ≠ Documento fiscal**: el documento fiscal es entidad separada con su propio ciclo de vida. |
