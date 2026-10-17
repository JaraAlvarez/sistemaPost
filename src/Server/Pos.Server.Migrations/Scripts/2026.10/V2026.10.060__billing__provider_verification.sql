-- =====================================================================================================
-- V2026.10.060 · billing · Conectar Factus desde la pantalla: verificación, salvaguardas de la cola y atraso retenido.
-- Diseño: docs/fases/pantallas-faltantes-propuesta.md §2 y la adenda (propuesta) del ADR-0059. Aún no desplegada: se corrigió en la ronda 2 (PA1).
--
--   · credential_hint: usuario enmascarado (p. ej. ti***@correo.co) para reconocer las credenciales sin mostrarlas; nunca la clave.
--   · last_verified_at / last_verification_ok / last_verification_message: última verificación contra el proveedor de las credenciales y
--     el ambiente VIGENTES. Encender exige una verificación correcta y la cola SOLO envía con modo <> OFF y verificación correcta.
--   · backlog_cutoff / backlog_released: la primera verificación correcta fija el corte; los documentos electrónicos emitidos ANTES no se
--     envían solos hasta que la administración elija "Enviar ahora" (released) o "No enviar" (estado NOT_SENT, que no se reintenta).
--   · backlog_environment / backlog_credentials_at: ambiente y llaves con que se fijó el corte; verificar en otro ambiente o con otras
--     llaves mueve el corte a ese momento (ronda 3, PB1).
--   · fiscal_documents.environment: ambiente con que se emitió y numeró cada documento electrónico (PB1). La cola solo envía los del
--     ambiente vigente; los de otro ambiente quedan retenidos y visibles ("No enviar"). Los existentes toman el ambiente vigente de su empresa.
--   · auto_disabled_at: la facturación que estaba encendida SIN verificación (instalaciones con llaves y modo <> OFF de cuando el adaptador
--     era NONE) se APAGA aquí; el tablero avisa "La facturación electrónica quedó apagada" hasta que se conecte de nuevo.
-- =====================================================================================================

ALTER TABLE billing.provider_settings
    ADD COLUMN credential_hint            varchar(80),
    ADD COLUMN last_verified_at           timestamptz,
    ADD COLUMN last_verification_ok       boolean,
    ADD COLUMN last_verification_message  varchar(500),
    ADD COLUMN backlog_cutoff             timestamptz,
    ADD COLUMN backlog_released           boolean        NOT NULL DEFAULT false,
    ADD COLUMN auto_disabled_at           timestamptz,
    ADD COLUMN backlog_environment        varchar(12),
    ADD COLUMN backlog_credentials_at     timestamptz,
    ADD CONSTRAINT ck_provider_settings__backlog_environment CHECK (backlog_environment IS NULL OR backlog_environment IN ('SANDBOX', 'PRODUCTION')),
    ADD CONSTRAINT ck_provider_settings__verification CHECK ((last_verified_at IS NULL) = (last_verification_ok IS NULL)),
    -- El atraso solo se libera después de una verificación correcta.
    ADD CONSTRAINT ck_provider_settings__backlog CHECK (NOT backlog_released OR backlog_cutoff IS NOT NULL);

COMMENT ON COLUMN billing.provider_settings.credential_hint IS 'Usuario del proveedor enmascarado (ti***@correo.co). Nunca la clave ni el client secret.';
COMMENT ON COLUMN billing.provider_settings.last_verified_at IS 'Última verificación contra el proveedor de las credenciales y el ambiente vigentes (se borra al cambiarlos).';
COMMENT ON COLUMN billing.provider_settings.last_verification_ok IS 'Resultado de la última verificación; encender y enviar la cola exigen true.';
COMMENT ON COLUMN billing.provider_settings.backlog_cutoff IS 'Primera verificación correcta: los documentos emitidos antes esperan "Enviar ahora" o "No enviar".';
COMMENT ON COLUMN billing.provider_settings.auto_disabled_at IS 'La migración V2026.10.060 apagó la facturación (estaba encendida sin verificación).';

-- Encendida sin verificación (no puede estarlo después de esta versión): se apaga y queda el aviso.
UPDATE billing.provider_settings
SET mode = 'OFF', auto_disabled_at = now(), updated_at = now()
WHERE mode <> 'OFF';

-- Ambiente de cada documento electrónico (PB1).
ALTER TABLE billing.fiscal_documents
    ADD COLUMN environment varchar(12),
    ADD CONSTRAINT ck_fiscal_documents__environment CHECK (environment IS NULL OR environment IN ('SANDBOX', 'PRODUCTION'));

COMMENT ON COLUMN billing.fiscal_documents.environment IS 'Ambiente del proveedor con que se emitió y numeró el documento electrónico; la cola solo envía los del ambiente vigente.';

UPDATE billing.fiscal_documents f
SET environment = COALESCE((SELECT ps.environment FROM billing.provider_settings ps WHERE ps.company_id = f.company_id), 'SANDBOX')
WHERE f.document_type <> 'INTERNAL_RECEIPT';

-- Estado NOT_SENT: atraso de antes de conectar (o de otro ambiente) que la administración decidió no enviar.
ALTER TABLE billing.fiscal_documents DROP CONSTRAINT ck_fiscal_documents__status;
ALTER TABLE billing.fiscal_documents
    ADD CONSTRAINT ck_fiscal_documents__status CHECK (status IN (
        'NOT_REQUIRED', 'PENDING', 'SUBMITTING', 'ACCEPTED', 'REJECTED', 'CONTINGENCY', 'ERROR', 'VOIDED', 'CANCELLED', 'NOT_SENT')),
    ADD CONSTRAINT ck_fiscal_documents__not_sent CHECK (status <> 'NOT_SENT' OR document_type <> 'INTERNAL_RECEIPT');
