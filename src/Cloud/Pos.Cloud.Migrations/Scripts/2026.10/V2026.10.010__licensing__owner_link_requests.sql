-- =====================================================================================================
-- V2026.10.010 · licensing · "Continuar con Google" en el instalador premium (Fase 13-B, parte E; docs/fases/fase-13b-propuesta.md
-- §9.5). Google no permite su ingreso dentro de un WebView incrustado: el instalador crea una solicitud con la clave, abre el
-- portal en el navegador del sistema y consulta hasta que un usuario CLIENTE de la cuenta dueña la autoriza (como OAuth 2.0
-- Device Authorization, RFC 8628). Al recogerla, la nube entrega UNA vez el mismo enlace firmado de /v1/owner-links.
-- Solo se guarda el HASH del secreto de consulta. Las filas no se borran (L-09): vencen a los 10 minutos y quedan como rastro.
-- =====================================================================================================

CREATE TABLE licensing.owner_link_requests (
    id                     uuid           NOT NULL,
    license_id             uuid           NOT NULL,
    organization_id        uuid           NOT NULL,
    poll_token_hash        char(64)       NOT NULL,
    user_code              char(9)        NOT NULL,
    status                 varchar(10)    NOT NULL,
    created_at             timestamptz    NOT NULL,
    expires_at             timestamptz    NOT NULL,
    created_ip             inet,
    poll_interval_seconds  smallint       NOT NULL,
    last_polled_at         timestamptz,
    portal_user_id         uuid,
    approved_email         varchar(120),
    approved_display_name  varchar(120),
    decided_at             timestamptz,
    decided_ip             inet,
    consumed_at            timestamptz,
    issued_token_id        uuid,
    CONSTRAINT pk_owner_link_requests PRIMARY KEY (id),
    CONSTRAINT ux_owner_link_requests__poll_token_hash UNIQUE (poll_token_hash),
    CONSTRAINT fk_owner_link_requests__license FOREIGN KEY (license_id) REFERENCES licensing.licenses (id),
    CONSTRAINT fk_owner_link_requests__organization FOREIGN KEY (organization_id) REFERENCES licensing.organizations (id),
    CONSTRAINT fk_owner_link_requests__portal_user FOREIGN KEY (portal_user_id) REFERENCES portal.portal_users (id),
    CONSTRAINT ck_owner_link_requests__status CHECK (status IN ('PENDING', 'APPROVED', 'DENIED', 'CONSUMED')),
    CONSTRAINT ck_owner_link_requests__poll_token_hash CHECK (poll_token_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_owner_link_requests__user_code CHECK (user_code ~ '^[A-Z0-9]{4}-[A-Z0-9]{4}$'),
    CONSTRAINT ck_owner_link_requests__expires CHECK (expires_at > created_at AND expires_at <= created_at + interval '1 hour'),
    CONSTRAINT ck_owner_link_requests__interval CHECK (poll_interval_seconds BETWEEN 1 AND 60),
    -- Decidida (autorizada, cancelada o ya entregada) = tiene quién y cuándo.
    CONSTRAINT ck_owner_link_requests__decided CHECK (
        (status = 'PENDING') = (decided_at IS NULL) AND (decided_at IS NULL) = (portal_user_id IS NULL)),
    -- Solo una autorizada lleva el correo y el nombre validados.
    CONSTRAINT ck_owner_link_requests__approved CHECK (
        (status IN ('APPROVED', 'CONSUMED')) = (approved_email IS NOT NULL)
        AND (approved_email IS NULL) = (approved_display_name IS NULL)
        AND (approved_email IS NULL OR approved_email = lower(approved_email))),
    -- Un solo uso: entregada = tiene la fecha y el jti del enlace firmado.
    CONSTRAINT ck_owner_link_requests__consumed CHECK (
        (status = 'CONSUMED') = (consumed_at IS NOT NULL) AND (consumed_at IS NULL) = (issued_token_id IS NULL))
);

CREATE INDEX ix_owner_link_requests__license ON licensing.owner_link_requests (license_id, expires_at);
CREATE INDEX ix_owner_link_requests__organization ON licensing.owner_link_requests (organization_id);
CREATE INDEX ix_owner_link_requests__portal_user ON licensing.owner_link_requests (portal_user_id) WHERE portal_user_id IS NOT NULL;

COMMENT ON TABLE licensing.owner_link_requests IS
    'Solicitudes de "Continuar con Google" del instalador premium: el dueño autoriza en el portal y el instalador recoge una vez el enlace firmado.';
COMMENT ON COLUMN licensing.owner_link_requests.poll_token_hash IS 'SHA-256 (hex) del secreto de consulta; el secreto solo lo tiene el instalador.';
