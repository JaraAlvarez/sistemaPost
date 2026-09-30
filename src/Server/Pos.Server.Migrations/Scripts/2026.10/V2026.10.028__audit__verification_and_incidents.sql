-- =====================================================================================================
-- V2026.10.028 · audit · Catálogo de acciones, verificaciones programadas e incidentes de integridad (Fase 10).
-- Diseño: docs/fases/fase-10-propuesta.md §4, D10-01, D10-04 y D10-05.
--
--   · action_types: nombre en español y severidad de cada acción (lo llena R__audit__action_types.sql desde el catálogo del código).
--   · verification_runs: cada verificación (diaria incremental, semanal completa o manual) con su resultado. Solo inserción.
--   · integrity_incidents: una verificación con hallazgos abre un incidente CRÍTICO; integrity_incident_acknowledgements guarda el
--     reconocimiento del propietario (una fila nueva, nunca una edición). Solo inserción.
--   · Índices para las consultas nuevas de la bitácora (severidad, caja, autorizador y texto).
-- =====================================================================================================

CREATE TABLE audit.action_types (
    code              varchar(60)   NOT NULL,
    module            varchar(30)   NOT NULL,
    name              varchar(150)  NOT NULL,
    default_severity  varchar(10)   NOT NULL,
    CONSTRAINT pk_action_types PRIMARY KEY (code),
    CONSTRAINT ck_action_types__code CHECK (code ~ '^[A-Z][A-Z0-9_]+$'),
    CONSTRAINT ck_action_types__severity CHECK (default_severity IN ('INFO', 'WARNING', 'CRITICAL'))
);

CREATE TABLE audit.verification_runs (
    id                uuid          NOT NULL,
    node_id           uuid          NOT NULL,
    company_id        uuid,
    kind              varchar(12)   NOT NULL,
    started_at        timestamptz   NOT NULL,
    finished_at       timestamptz   NOT NULL,
    from_seal_no      bigint,
    last_seal_no      bigint,
    last_seal_code    varchar(19),
    seals_checked     integer       NOT NULL,
    rows_checked      bigint        NOT NULL,
    unsealed_rows     bigint        NOT NULL,
    is_valid          boolean       NOT NULL,
    findings_count    integer       NOT NULL,
    findings          jsonb         NOT NULL,
    requested_by      uuid,
    CONSTRAINT pk_verification_runs PRIMARY KEY (id),
    CONSTRAINT ck_verification_runs__kind CHECK (kind IN ('INCREMENTAL', 'FULL', 'MANUAL')),
    CONSTRAINT ck_verification_runs__counts CHECK (seals_checked >= 0 AND rows_checked >= 0 AND unsealed_rows >= 0 AND findings_count >= 0),
    CONSTRAINT ck_verification_runs__valid CHECK (is_valid = (findings_count = 0)),
    CONSTRAINT ck_verification_runs__times CHECK (finished_at >= started_at)
);

CREATE INDEX ix_verification_runs__node_started ON audit.verification_runs (node_id, started_at DESC);

CREATE TABLE audit.integrity_incidents (
    id                   uuid          NOT NULL,
    node_id              uuid          NOT NULL,
    company_id           uuid,
    verification_run_id  uuid          NOT NULL,
    detected_at          timestamptz   NOT NULL,
    findings_count       integer       NOT NULL,
    summary              varchar(500)  NOT NULL,
    CONSTRAINT pk_integrity_incidents PRIMARY KEY (id),
    CONSTRAINT ux_integrity_incidents__run UNIQUE (verification_run_id),
    CONSTRAINT fk_integrity_incidents__run FOREIGN KEY (verification_run_id) REFERENCES audit.verification_runs (id),
    CONSTRAINT ck_integrity_incidents__findings CHECK (findings_count > 0)
);

CREATE TABLE audit.integrity_incident_acknowledgements (
    id               uuid          NOT NULL,
    incident_id      uuid          NOT NULL,
    acknowledged_by  uuid          NOT NULL,
    acknowledged_at  timestamptz   NOT NULL,
    note             varchar(1000) NOT NULL,
    CONSTRAINT pk_integrity_incident_acknowledgements PRIMARY KEY (id),
    CONSTRAINT ux_integrity_incident_acknowledgements__incident UNIQUE (incident_id),
    CONSTRAINT fk_integrity_incident_acknowledgements__incident FOREIGN KEY (incident_id) REFERENCES audit.integrity_incidents (id),
    CONSTRAINT fk_integrity_incident_acknowledgements__user FOREIGN KEY (acknowledged_by) REFERENCES identity.users (id),
    CONSTRAINT ck_integrity_incident_acknowledgements__note CHECK (length(btrim(note)) >= 10)
);

CREATE INDEX ix_integrity_incident_acknowledgements__acknowledged_by ON audit.integrity_incident_acknowledgements (acknowledged_by);

-- Solo inserción (RN-AUD-03), igual que la bitácora.
CREATE TRIGGER trg_verification_runs_append_only
    BEFORE UPDATE OR DELETE ON audit.verification_runs
    FOR EACH ROW EXECUTE FUNCTION audit.fn_append_only();

CREATE TRIGGER trg_integrity_incidents_append_only
    BEFORE UPDATE OR DELETE ON audit.integrity_incidents
    FOR EACH ROW EXECUTE FUNCTION audit.fn_append_only();

CREATE TRIGGER trg_integrity_incident_acknowledgements_append_only
    BEFORE UPDATE OR DELETE ON audit.integrity_incident_acknowledgements
    FOR EACH ROW EXECUTE FUNCTION audit.fn_append_only();

-- Consultas nuevas de la bitácora (D10-02 / §5.2).
CREATE INDEX ix_audit_log__severity ON audit.audit_log (severity, occurred_at) WHERE severity <> 'INFO';
CREATE INDEX ix_audit_log__authorized_by ON audit.audit_log (authorized_by, occurred_at) WHERE authorized_by IS NOT NULL;
CREATE INDEX ix_audit_log__terminal ON audit.audit_log (pos_terminal_id, occurred_at) WHERE pos_terminal_id IS NOT NULL;
CREATE INDEX ix_audit_log__text ON audit.audit_log
    USING gin ((COALESCE(summary, '') || ' ' || COALESCE(entity_label, '')) gin_trgm_ops);
