-- =====================================================================================================
-- V2026.10.102 · sales · Ventas de titulares ya suprimidos (revisión S1, ronda 2).
--
-- Los clientes anonimizados ANTES de esta versión tienen sus ventas en la nube con el nombre (y la cédula). Se marcan ahora para que la
-- sincronización las reenvíe con versión nueva y sin el nombre; la nube además quita customerIdentification de lo ya recibido
-- (V2026.10.015 de la nube). La venta local conserva su snapshot (retención legal).
-- =====================================================================================================

UPDATE sales.sales s
SET customer_redacted_at = now()
FROM customers.customers c
WHERE c.party_id = s.customer_id
  AND c.anonymized_at IS NOT NULL
  AND s.customer_redacted_at IS NULL;
