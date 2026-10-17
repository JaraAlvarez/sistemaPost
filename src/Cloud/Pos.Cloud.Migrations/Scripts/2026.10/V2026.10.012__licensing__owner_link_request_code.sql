-- =====================================================================================================
-- V2026.10.012 · licensing · "Continuar con Google" del instalador: el dueño ESCRIBE en el portal el código que muestra su
-- instalador (antes solo lo comparaba a simple vista). Mitiga el "phishing" del flujo tipo Device Authorization: quien tenga la clave
-- y le envíe al dueño el enlace de su propia solicitud ya no obtiene la autorización con un solo clic. Tras 5 códigos incorrectos la
-- solicitud queda cancelada (DENIED). La vigencia baja a 5 minutos (la regla de "máximo 1 hora" no cambia).
-- =====================================================================================================

ALTER TABLE licensing.owner_link_requests
    ADD COLUMN code_attempts smallint NOT NULL DEFAULT 0;

ALTER TABLE licensing.owner_link_requests
    ADD CONSTRAINT ck_owner_link_requests__code_attempts CHECK (code_attempts BETWEEN 0 AND 20);

COMMENT ON COLUMN licensing.owner_link_requests.code_attempts IS 'Códigos incorrectos escritos en el portal; al llegar al máximo la solicitud se cancela.';
