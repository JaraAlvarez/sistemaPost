# Fase 17-B · Interfaz sin ventanas: caja y administración — Informe

- **Estado:** Implementada — pendiente de tu validación · 2026-10-16
- **Propuesta:** [fase-17b-propuesta.md](fase-17b-propuesta.md)

## 1. Qué se entregó

### Caja
| Antes (ventana) | Ahora |
|---|---|
| Cobrar (F10) | Columna derecha: total, recibido, cambio, medios, valores rápidos, confirmar (F10). La venta sigue a la vista |
| Cliente (F3) | Columna derecha con búsqueda y lista |
| Ventas suspendidas (F9) | Columna derecha con la lista |
| Abrir jornada | Pantalla completa con el conteo por **monto** |
| Cerrar jornada y reporte Z | Pantalla completa |
| Buscar producto (F2) | Lista bajo el buscador mientras se escribe el nombre; F2 vuelve al buscador |

Siguen como ventana (acciones cortas): cantidad (F4), descuento (F6), cancelar venta (F7), autorización de supervisor, ayuda (F1).

Arreglos encontrados en el recorrido: el escáner no tenía el foco al entrar (el primer código se perdía); el cambio tardaba varios segundos
en mostrarse porque la caja esperaba a la impresora (ahora imprime aparte y avisa si falla).

### Administración (21 ventanas → 14 páginas)
| Lista | Páginas nuevas |
|---|---|
| Compras | `/admin/compras/nueva` (también `?orden=`), `/admin/compras/{id}`, `/admin/compras/ordenes/nueva`, `/admin/compras/ordenes/{id}` |
| Cuentas por pagar | `/admin/cuentas-por-pagar/pago` (`?proveedor=&factura=` preseleccionan) |
| Conteos · Traslados · Ajustes | `/admin/conteos/{id}`, `/admin/traslados/{id}`, `/admin/ajustes/{id}` y su `/nuevo` |
| Productos | `/admin/productos/{id}` — **ficha nueva**: Datos (editar), Precios (historial y cancelar programados), Códigos de barras (agregar, generar, quitar), Impuestos, Existencias por bodega; activar, inactivar, descontinuar |
| Proveedores | `/admin/proveedores/{id}`: Datos y Estado de cuenta; activar, bloquear, inactivar |
| Clientes | `/admin/clientes/{id}`: Datos, grupo y lista de precios, Compras, Historial; corregir, bloquear, activar |
| Promociones | `/admin/promociones/{id}`: Datos (borrador) o Resumen, Simular; activar, pausar, terminar |
| Jornadas | `/admin/jornadas/{id}`: totales, movimientos, reporte X/Z, revisar y cierre por supervisor (sección de la página) |

- Las listas abren el detalle al hacer clic en la fila; "Nuevo" abre la página vacía y al guardar pasa a la del documento.
- **Migas de pan** de las páginas de detalle: Inicio › Grupo › Lista (enlace) › Documento (`Navegacion.Buscar` por prefijo, `Pagina.Miga`).
- No cambió el servidor ni la API.

## 2. Validación hecha

- Compilación sin errores ni advertencias.
- **Caja en el navegador:** entrar → el escáner tiene el foco → escanear → F10 → F10 cobra en 1,5 s y el foco vuelve al escáner; F3, F9
  y F10 en la columna derecha sin ventanas; cierre a pantalla completa con montos (`50.000` en monedas de $ 200 = 250 monedas; un monto
  que no es múltiplo se marca); "Volver a vender".
- **Administración en el navegador:** las 10 páginas "Nuevo" cargan con sus migas; abren los detalles de producto, compra, proveedor,
  jornada y ajuste; crear un conteo lleva a `/admin/conteos/{id}`, iniciarlo deja el foco en el lector.
- **No probado aquí:** guardar en cada formulario (producto, proveedor, cliente, promoción, compra, orden, pago, traslado, ajuste) y las
  acciones de cada documento. Las peticiones son las mismas que tenían las ventanas.

## 3. Pendiente / mejoras detectadas
Ver el mensaje de cierre de la fase (lista de mejoras propuestas).
