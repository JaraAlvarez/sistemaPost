-- =====================================================================================================
-- V2026.10.101 · sales · Supresión del titular propagada a la copia de la venta en la nube (revisión S1,
-- docs/fases/fase-sync-pagos-endurecimiento.md §1).
--
-- customer_redacted_at: el titular de los datos pidió la supresión (Customers, AnonymizeCustomerHandler). La venta LOCAL conserva su
-- snapshot durante la retención legal, pero la sincronización ya no sube el nombre del cliente y usa esta marca como versión nueva,
-- para que la nube reemplace su copia por una anonimizada.
-- =====================================================================================================

ALTER TABLE sales.sales
    ADD COLUMN customer_redacted_at timestamptz;

COMMENT ON COLUMN sales.sales.customer_redacted_at IS
    'Supresión del titular (Ley 1581): la venta sube a la nube sin el nombre del cliente desde este momento.';
