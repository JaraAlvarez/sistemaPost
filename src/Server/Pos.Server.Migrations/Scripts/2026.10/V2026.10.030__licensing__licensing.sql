-- =====================================================================================================
-- V2026.10.030 · licensing · Licencia dentro del POS (Fase 12-B). Diseño: docs/fases/fase-12b-propuesta.md §5, D12B-01.
--
--   · license_state: una fila por nodo con el token firmado de la nube, la hora confiable y la mayor hora observada. El ESTADO
--     (DEMO, VALID, GRACE, RESTRICTED…) no se guarda como verdad: se calcula del token firmado, la huella y el reloj
--     (last_state solo sirve para auditar los cambios). Viaja en los backups: restaurado en otro PC, la huella no coincide y
--     pide reactivación.
--   · checkins: cada intento de activación, verificación o liberación. Solo inserción.
-- No se sincronizan (son datos del nodo).
-- =====================================================================================================

CREATE SCHEMA IF NOT EXISTS licensing;

CREATE TABLE licensing.license_state (
    node_id                uuid          NOT NULL,
    token                  text,
    license_key_prefix     varchar(12),
    activated_at           timestamptz,
    last_checkin_at        timestamptz,
    last_checkin_error     varchar(500),
    revoked                boolean       NOT NULL DEFAULT false,
    reactivation_required  boolean       NOT NULL DEFAULT false,
    deactivated            boolean       NOT NULL DEFAULT false,
    clock_offset_seconds   bigint        NOT NULL DEFAULT 0,
    max_observed_utc       timestamptz   NOT NULL,
    last_state             varchar(24),
    created_at             timestamptz   NOT NULL,
    updated_at             timestamptz   NOT NULL,
    CONSTRAINT pk_license_state PRIMARY KEY (node_id),
    CONSTRAINT ck_license_state__token_length CHECK (token IS NULL OR length(token) <= 8192)
);

COMMENT ON TABLE licensing.license_state IS 'Token de licencia firmado del nodo y reloj confiable (Fase 12-B). El estado se calcula, no se guarda.';

CREATE TABLE licensing.checkins (
    id              uuid          NOT NULL,
    node_id         uuid          NOT NULL,
    occurred_at     timestamptz   NOT NULL,
    kind            varchar(12)   NOT NULL,
    succeeded       boolean       NOT NULL,
    error_code      varchar(60),
    subscription_status varchar(12),
    duration_ms     integer       NOT NULL,
    CONSTRAINT pk_checkins PRIMARY KEY (id),
    CONSTRAINT ck_checkins__kind CHECK (kind IN ('ACTIVATION', 'SCHEDULED', 'MANUAL', 'STARTUP', 'DEACTIVATION')),
    CONSTRAINT ck_checkins__outcome CHECK (succeeded OR error_code IS NOT NULL)
);

CREATE INDEX ix_checkins__node_occurred ON licensing.checkins (node_id, occurred_at DESC);

COMMENT ON TABLE licensing.checkins IS 'Intentos de activación, verificación y liberación con el servidor de licencias. Solo inserción.';
