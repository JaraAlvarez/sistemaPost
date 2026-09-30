-- =====================================================================================================
-- V2026.10.026 · cash · Tipos de medio de pago reservados para crédito y puntos (Fase 8, bloque 8.5).
-- Diseño: docs/fases/fase-08-propuesta.md §5.6, D8-15 y D8-16.
--
-- CUSTOMER_CREDIT (venta a crédito, fiado) y LOYALTY_POINTS (redención de puntos) quedan admitidos por la BD para que activarlos
-- en la Fase 8-B no cambie el esquema de la caja ni de las ventas. Hoy la API rechaza crear medios de esos tipos
-- (CASH.PAYMENT_KIND_NOT_AVAILABLE) y la venta, si llegara uno, lo rechaza con la implementación nula.
-- =====================================================================================================

ALTER TABLE cash.payment_methods DROP CONSTRAINT ck_payment_methods__kind;
ALTER TABLE cash.payment_methods ADD CONSTRAINT ck_payment_methods__kind
    CHECK (kind IN ('CASH', 'DEBIT_CARD', 'CREDIT_CARD', 'TRANSFER', 'WALLET', 'VOUCHER', 'OTHER', 'EXCHANGE_CREDIT', 'CUSTOMER_CREDIT', 'LOYALTY_POINTS'));
