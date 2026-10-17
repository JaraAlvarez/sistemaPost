# Plan · Variantes (talla y color) para ropa, calzado y deportes

> Estado: **PLAN — sin implementar** · 2026-10-17. Origen: el propietario quiere ofrecer BusinessPost a cualquier comercio (ropa,
> zapatos, deportes), no solo a supermercados. Desde la Fase 13-B los textos dicen "tu negocio".

## Qué falta hoy

Cada producto es una sola referencia con sus presentaciones (unidad, caja x 12). Una tienda de ropa necesita **una prenda con
varias combinaciones** (Camiseta Nike · talla M · negro), cada una con su **código de barras, existencias y, a veces, precio**.

## Propuesta

- **Producto padre + variantes:** atributos configurables por categoría (Talla, Color, Material); el sistema genera la matriz
  (S/M/L × Negro/Blanco) y cada combinación es un SKU con su código de barras.
- **Inventario por variante** (kardex, conteos, traslados y ajustes ya trabajan por producto: la variante se vuelve el producto que
  se mueve; el padre agrupa para reportes).
- **Caja:** escanear el código de la variante la agrega directo; buscar por nombre muestra la matriz para elegir talla y color.
- **Compras:** cargar la factura por matriz (cantidades por talla y color en una sola grilla).
- **Etiquetas:** impresión de etiquetas con código de barras por variante.
- **Reportes:** ventas y existencias por talla/color ("¿qué talla se vende más?").
- **Cambios y devoluciones** (muy comunes en ropa): ya existen; se agrega "cambio por otra talla" en un paso.
- **tutiendanueva.com** ya maneja variantes (`ProductVariant`): la sincronización del plan 7-B §6.2 las emparejaría por SKU.

## Tamaño

Grande (catálogo, inventario, caja, compras, reportes, etiquetas). Recomendado como fase propia después del piloto.
