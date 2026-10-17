# Fase 17-B · Interfaz sin ventanas: caja y administración — Propuesta

> Estado: **APROBADA** · 2026-10-16 (pediste: "empieza con las de caja, luego las de administración").
> Base: Fase 15 (interfaz), Fase 17 (backoffice), rediseño BusinessPost (`docs/guia-diseno.md`).

## 0. Objetivo

Las ventanas emergentes tapaban la venta o el documento, se perdía lo escrito al cerrarlas sin querer y no tenían enlace propio. Después
de esta fase:

- **Caja:** nada se abre encima de la venta salvo las acciones de un campo (cantidad, descuento, cancelar, supervisor, ayuda).
- **Administración:** cada documento o ficha tiene su página con dirección propia (`/admin/compras/{id}`), migas de pan hasta la lista
  y botón Atrás del navegador. Las confirmaciones cortas siguen como ventana.

No cambia el servidor, la base de datos ni la API: es solo interfaz.

## 1. Decisiones

| # | Decisión | Motivo |
|---|---|---|
| D17B-01 | **Caja:** cobro (F10), cliente (F3) y suspendidas (F9) cambian la **columna derecha**; Esc vuelve | La venta sigue a la vista mientras se cobra o se busca el cliente |
| D17B-02 | **Caja:** abrir jornada y cierre con reporte Z ocupan **la pantalla** | Son el primer y el último paso del día; llevan el conteo por denominación |
| D17B-03 | **Caja:** el buscador acepta código o **nombre** y muestra la **lista bajo el campo**; F2 vuelve al buscador | Sin ventana de búsqueda; el lector (solo números) no cambia |
| D17B-04 | **Administración:** lista → página de detalle `/admin/<lista>/{id}`; "Nuevo" → `/admin/<lista>/nuevo` | Enlace propio, recargar sin perder, Atrás funciona |
| D17B-05 | Las **migas de pan** reconocen las páginas de detalle (`Navegacion.Buscar` por prefijo y `Pagina.Miga`) | Inicio › Grupo › Lista › Documento, con vuelta a la lista |
| D17B-06 | Se quedan como ventana: confirmar con motivo (anular, terminar, bloquear, revocar) y formularios de 1–4 campos (caja, impresora, sucursal, bodega, medio de pago, parámetro, grupo, categoría, gasto, usuario, ajuste rápido, código de emparejamiento) | Son rápidos y no tapan trabajo largo |

## 2. Páginas nuevas de la administración

| Lista | Páginas |
|---|---|
| Compras | `/admin/compras/nueva`, `/admin/compras/{id}`, `/admin/compras/ordenes/nueva`, `/admin/compras/ordenes/{id}` |
| Cuentas por pagar | `/admin/cuentas-por-pagar/pago` |
| Conteos · Traslados · Ajustes | `/admin/conteos/{id}`, `/admin/traslados/{id}`, `/admin/ajustes/{id}` (y `/nuevo`) |
| Productos | `/admin/productos/{id}` (**ficha nueva**: datos, precios, códigos, impuestos, existencias) |
| Proveedores · Clientes | `/admin/proveedores/{id}`, `/admin/clientes/{id}` (y `/nuevo`) |
| Promociones | `/admin/promociones/{id}` (con el simulador) |
| Jornadas | `/admin/jornadas/{id}` (con el cierre por supervisor) |

## 3. Validación

Compilación; recorrido en el navegador de la caja (entrar, escanear, cobrar, cliente, suspendidas, cierre) y de cada página nueva de la
administración. Tú: el recorrido de la guía del piloto §3 con la interfaz nueva.
