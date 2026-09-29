# ADR-0032 · Pagos combinados, redondeo del efectivo a $50 y cambio

- **Estado:** Aceptada · 2026-09-29 · Fase 7 · Decisiones D7-08 y D7-09 (pregunta 3 del propietario)

## Contexto
Los productos por peso producen valores como $6.225 que no se pueden pagar en efectivo (la moneda más pequeña es $50). El cierre de
caja (ADR-0027) compara esperado y contado **por medio de pago**: si un datáfono "diera cambio", el efectivo del cajón no cuadraría.

## Decisión
- `PaymentAllocator` (dominio): primero los medios que no dan cambio, que **no pueden exceder el saldo** (`SALES.NON_CASH_OVERPAYMENT`,
  RN-SAL-09) y exigen referencia si el medio lo pide (`SALES.REFERENCE_REQUIRED`, RN-SAL-10); el efectivo, **en un solo pago**
  (`SALES.SINGLE_CASH_TENDER`), cubre el resto y da el cambio. Σ aplicado = total; en la BD, CHECK pagado − cambio = total y cambio
  solo en medios que afectan el cajón.
- **Redondeo del efectivo** al múltiplo de `sales.cash_rounding_increment` ($50): solo sobre la parte pagada en efectivo, registrado
  como `rounding_adjustment` de la venta (puede subir o bajar el total). Con tarjeta o transferencia se cobra exacto.
- Movimiento de caja `SALE` por medio = lo aplicado (en efectivo, entregado − cambio): lo que queda en el cajón.
- La tarjeta guarda franquicia y últimos 4 dígitos, **nunca** el número completo.
- El medio del sistema **"Crédito por cambio"** (código `CAMBIO`, tipo `EXCHANGE_CREDIT`) no afecta el cajón, no da cambio, no se cuenta
  en el arqueo y solo lo aplica un cambio de mercancía (ADR-0033); no se crean otros medios de ese tipo.

## Consecuencias
- ✅ El cierre por medio cuadra al peso; el efectivo siempre es pagable con billetes y monedas.
- ⚠️ El ajuste de redondeo aparece en la venta y en los reportes (se suma y resta en el cuadre).
