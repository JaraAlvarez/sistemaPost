-- =====================================================================================================
-- V2026.10.013 · licensing · Endurecimiento del pago en línea (revisión W1–W3 y hallazgos bajos,
-- docs/fases/fase-sync-pagos-endurecimiento.md §2).
--
--   · payment_orders.result_token: 128 bits aleatorios de la página de regreso /pagar/resultado/<referencia>/<token> (la referencia
--     es un consecutivo). Nulo en las órdenes anteriores: su página ya no muestra estado ni valor.
--   · payment_orders.granted_period_* / previous_period_* / reverted_at: el periodo que dio la renovación y el que había antes, para
--     DEVOLVER la vigencia si la pasarela anula (VOIDED) un pago ya aplicado. Una orden revertida queda VOIDED con applied_at.
--   · payment_events.source_ip: IP del webhook; limita la auditoría de los eventos con firma inválida (de esos solo se guardan metadatos).
--   · subscription_events: tipo RENEWAL_REVERTED.
-- =====================================================================================================

ALTER TABLE licensing.payment_orders
    ADD COLUMN result_token          varchar(32),
    ADD COLUMN granted_period_start  timestamptz,
    ADD COLUMN granted_period_end    timestamptz,
    ADD COLUMN previous_period_start timestamptz,
    ADD COLUMN previous_period_end   timestamptz,
    ADD COLUMN reverted_at           timestamptz,
    ADD CONSTRAINT ck_payment_orders__result_token CHECK (result_token IS NULL OR result_token ~ '^[A-Za-z0-9_-]{22}$'),
    ADD CONSTRAINT ck_payment_orders__granted CHECK (
        (granted_period_start IS NULL) = (granted_period_end IS NULL)
        AND (granted_period_end IS NULL OR granted_period_end > granted_period_start)),
    ADD CONSTRAINT ck_payment_orders__reverted CHECK (
        reverted_at IS NULL OR (status = 'VOIDED' AND applied_at IS NOT NULL AND granted_period_end IS NOT NULL));

ALTER TABLE licensing.payment_orders DROP CONSTRAINT ck_payment_orders__applied;
ALTER TABLE licensing.payment_orders ADD CONSTRAINT ck_payment_orders__applied CHECK (
    (applied_at IS NULL) = (renewal_event_id IS NULL)
    AND (applied_at IS NULL OR (gateway_transaction_id IS NOT NULL
         AND (status IN ('APPROVED', 'REVIEW_REQUIRED') OR (status = 'VOIDED' AND reverted_at IS NOT NULL))))
    AND (status <> 'APPROVED' OR applied_at IS NOT NULL));

CREATE UNIQUE INDEX ux_payment_orders__result_token ON licensing.payment_orders (result_token) WHERE result_token IS NOT NULL;

ALTER TABLE licensing.payment_events
    ADD COLUMN source_ip inet;

CREATE INDEX ix_payment_events__rejected_ip ON licensing.payment_events (source_ip, received_at) WHERE signature_valid = false;

ALTER TABLE licensing.subscription_events DROP CONSTRAINT ck_subscription_events__type;
ALTER TABLE licensing.subscription_events ADD CONSTRAINT ck_subscription_events__type CHECK (type IN (
    'CREATED', 'RENEWED', 'EDITION_CHANGED', 'SUSPENDED', 'REACTIVATED', 'CANCELLED', 'GRACE_EXTENDED', 'STATUS_CHANGED', 'RENEWAL_REVERTED'));
ALTER TABLE licensing.subscription_events ADD CONSTRAINT ck_subscription_events__reverted CHECK (
    type <> 'RENEWAL_REVERTED' OR (payment_reference IS NOT NULL AND period_start IS NOT NULL AND period_end IS NOT NULL));
