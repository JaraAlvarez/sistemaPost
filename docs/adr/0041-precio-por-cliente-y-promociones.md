# ADR-0041 · Precio por cliente (listas fijas y derivadas) y promociones

- **Estado:** Aceptada · 2026-09-29 · Fase 8 · Decisiones D8-09 a D8-12 (preguntas 3 y 4 del propietario)

## Decisión
- **Resolución del precio:** precio abierto/modificado > **lista del cliente** > **lista de su grupo** > **lista general**; dentro de
  la lista, el precio de la sucursal gana al de todas. La lista se fija en la venta al asignar el cliente y **cambiar el cliente
  re-precia las líneas activas**, salvo precio abierto, modificado y etiqueta de báscula por precio (RN-PRL-02).
- **Listas derivadas:** `catalog.price_lists.adjustment_percent` (% sobre la general, −90 a +100) con `rounding_increment` (por
  defecto $50, al más cercano). Los precios fijos cargados en la lista ganan al porcentaje. Sin precio en una lista fija → general.
- **Promociones sin tocar el motor:** `SaleCalculator` recibe el precio de la lista y `PromotionsAllowed` = la lista admite promociones
  (`allows_promotions`, por defecto sí). El precio especial y el precio por cantidad solo aplican si mejoran; el porcentaje se aplica
  sobre el precio de la lista; el descuento manual va después.
- **Snapshot:** la venta guarda `price_list_id`, `price_list_code`, `customer_group_code` y `list_allows_promotions`; la línea guarda
  la lista y el **origen del precio** (`LIST`, `DERIVED`, `DEFAULT`, `OPEN`, `OVERRIDE`, `SCALE_LABEL`).
- Solo propietario y administrador asignan listas (`customers.pricing.assign`, auditado).

## Consecuencias
- "¿Por qué se cobró este precio?" se responde con la línea. Las ventas anteriores quedaron con la lista general.
- El catálogo expone `ICatalogSaleItems.GetPriceListAsync` y un `priceListId` opcional al resolver un producto.
