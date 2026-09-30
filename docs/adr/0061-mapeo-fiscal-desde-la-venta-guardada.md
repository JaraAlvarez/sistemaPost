# ADR-0061 · Mapeo fiscal desde la venta guardada

- **Estado:** Aceptada · 2026-09-30 · Fase 11-B · Decisiones D11B-06 y D11B-07 y documento soporte (pregunta 5) de la
  [propuesta](../fases/fase-11b-propuesta.md)
- **Relacionadas:** [ADR-0005](0005-dinero-decimal-redondeo.md) (dinero y redondeo), [ADR-0030](0030-venta-persistida-y-motor-de-calculo-puro.md)
  (venta inmutable), [ADR-0032](0032-pagos-redondeo-del-efectivo-y-cambio.md) (redondeo del efectivo)

## Contexto
La factura debe reflejar **exactamente lo cobrado** (RN-FE-03). Factus calcula él mismo bruto, base e impuestos (cada impuesto por
separado con redondeo bancario), admite **máximo dos decimales** en cantidad y precio, recibe la bolsa plástica como un ítem y no admite
todos los impuestos del POS como tarifa. El POS vende por peso con tres o cuatro decimales y redondea el efectivo a $50.

## Decisión
1. **Modelo fiscal neutro** (`FiscalModel.cs`, dominio de Billing): borradores de factura, nota crédito, documento soporte y nota de
   ajuste con emisor, establecimiento, adquirente/proveedor, renglones, impuestos por tarifa, medios de pago con código DIAN y totales. Lo
   arma `FiscalDraftBuilder` **desde el documento guardado** (venta, anulación, cambio, garantía o compra), nunca desde el catálogo actual,
   y **concilia** renglones + redondeo contra lo cobrado: si no cuadran, el documento no sale (`BILLING.TOTALS_MISMATCH`). Cada adaptador
   traduce el modelo a su API (`FactusDraftMapper` para Factus: puro y determinista).
2. **Adquirente:** cliente identificado con los datos fiscales de la Fase 8 (o los corregidos por el supervisor, guardados en
   `fiscal_documents.buyer_fiscal`; la venta no cambia) o **consumidor final** (cédula "13", 222222222222, persona natural, tributo "ZZ",
   responsabilidad R-99-PN). La factura se envía por correo solo si el cliente identificado tiene correo.
3. **Bolsa como ítem:** el impuesto nacional al consumo de bolsas plásticas sale del renglón del producto y se vuelve un renglón propio con
   su valor (IVA excluido); el producto "bolsa" en cero que solo lleva el impuesto no se envía.
4. **Impuestos:** IVA "01" (exento = tarifa 0; excluido = `is_excluded`), INC "04" y ultraprocesados "35" como tarifas. Los que Factus no
   recibe como tarifa (valor fijo por unidad, otros códigos; en el documento soporte todo lo que no sea IVA) **se suman a la base** del
   ítem para que el total no cambie.
5. **Base exacta:** por ítem se envía `price` sin impuestos ni descuentos, `quantity` y `discount_amount` tales que
   `price × quantity − discount_amount` = la base gravable guardada; el descuento con IVA incluido se lleva a la base en proporción.
6. **Pesables sin redondear cantidades**, con cuatro estrategias en orden (la primera exacta gana):
   1. la cantidad tal cual si el precio sin impuestos resulta exacto a 2 decimales;
   2. la **unidad menor** (kg → g "GRM", L → mL, m → cm): 1,235 kg = 1235 g;
   3. cantidad entera: precio al centavo superior y la diferencia (< 1 centavo por unidad) al descuento;
   4. **por valor**: cantidad 1, unidad "94", precio = valor de la línea, y la cantidad real, la unidad y el precio en la **nota del ítem**.
7. **Redondeo del efectivo y centavos:** la diferencia entre lo pagado y el total que calculará Factus (redondeo a $50 + diferencias de
   centavos entre el redondeo bancario de Factus y el del POS) viaja en **`cash_rounding_amount`**. La nota crédito no lo admite: sus
   medios de pago se ajustan al total de Factus (la diferencia va al efectivo o al medio de mayor valor).
8. **Medios de pago:** uno por medio con su código DIAN (el configurado en caja o, si falta, por su clase); crédito (fiado) con forma "2" y
   vencimiento a ⚙️ 30 días.
9. **Notas crédito** (concepto DIAN 13.2.4): anulación → concepto 2 con todos los renglones; cambio de mercancía y reintegro por garantía →
   concepto 1 (devolución parcial) con las unidades devueltas al precio pagado e impuestos proporcionales. Referencian la factura aceptada
   (`bill_number`) y omiten el adquirente (Factus lo toma de la factura).
10. **Documento soporte** (`SUPPORT_DOCUMENT`, origen `PURCHASE`) al contabilizar una compra marcada "requiere documento soporte" con la
    facturación encendida: proveedor no obligado a facturar, renglones al costo. Factus solo admite NIT o documentos de extranjeros para el
    proveedor: una **cédula se envía como NIT "31"** con el mismo número; sin dirección, "No informada". **Anular la compra:** si el
    documento soporte no fue aceptado se cancela; si ya fue aceptado se emite una **nota de ajuste** (`ADJUSTMENT_NOTE`, origen
    `PURCHASE_VOID`, motivo 2) que espera a que el soporte esté aceptado.

## Consecuencias
- ✅ La base y el total que ve la DIAN son los de los libros; el POS nunca redondea cantidades ni cambia lo cobrado.
- ✅ El modelo neutro permite otro adaptador (p. ej. un proveedor con DEE POS) sin tocar ventas ni compras.
- ⚠️ Varias reglas de Factus son **supuestos** tomados de su documentación (consumidor final, unidades, nota "por valor", impuestos
  sumados a la base, cédula como NIT, 201/200/422…): se verifican en la prueba conjunta en el sandbox real ([guía Factus](../guia-factus.md) §9).
- ⚠️ La estrategia "por valor" deja la cantidad real solo en la nota del ítem; el contador debe aceptarlo para los pesables que la usen.
