# ADR-0034 · Promociones automáticas: una por línea, la más favorable, sin acumular

- **Estado:** Aceptada · 2026-09-29 · Fase 7 · Decisión D7-16 (preguntas 4 y 5 del propietario; resolución §15.4)

## Contexto
El propietario quiere promociones administradas por un encargado (no por la caja), aplicadas solas al escanear y fáciles de explicar
en el tiquete. Acumular promociones o depender de prioridades manuales produce resultados que ni el cliente ni la cajera entienden.

## Decisión
- Módulo propio `Promotions` (esquema `promotions`, migración `V2026.10.016`) administrado con `promotions.promotion.manage` por el rol
  nuevo `PROMOTIONS_MANAGER` (y propietario/administrador). Ciclo `DRAFT → ACTIVE → PAUSED → ENDED`; **una activa no se edita**
  (`PROMOTIONS.NOT_EDITABLE`): se pausa o termina y se crea otra. Simulador con el mismo motor de la caja y reporte de ventas y
  descuento por promoción.
- Tipos: `MULTI_BUY` (así en código y BD para "lleve N pague M"), `SPECIAL_PRICE`, `PERCENT_OFF`, `QUANTITY_PRICE` y `COMBO`. A qué
  aplica: producto (y presentación), categoría con sus subcategorías o marca. Vigencia, días de la semana, horario (admite el que pasa
  la medianoche, hora de Colombia), sucursales (todas o lista) y límite de aplicaciones por venta.
  - Lleve N pague M: por cada grupo de N unidades salen gratis las N − M de menor precio.
  - Precio por cantidad: desde X unidades, cada una a un precio.
  - **Combo**: dos o más **productos distintos** (la cantidad de cada uno va en su componente; los repetidos se rechazan con
    `PROMOTIONS.INVALID_RULE`); el precio del combo se reparte entre los componentes por su valor.
- Evaluación en el motor puro (ADR-0030) en cada recálculo, con las promociones que rigen en ese instante: **una promoción por línea**
  (no por unidad): en cada ronda gana la que más descuenta sobre las líneas libres y esas líneas quedan tomadas; en empate, la de menor
  Id. Los descuentos manuales autorizados se aplican después; una línea con precio modificado a mano no recibe promociones. La
  promoción aplicada queda en la línea (id, texto, valor). Si una promoción deja de regir con la venta abierta, el siguiente recálculo
  (a más tardar al cobrar) la quita.

## Consecuencias
- ✅ Resultado predecible y explicable; la caja no decide precios.
- ⚠️ Sin "promo sobre promo"; al asignar por línea, una línea no se reparte entre dos promociones.
