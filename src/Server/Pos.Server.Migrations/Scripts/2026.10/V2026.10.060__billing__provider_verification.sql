-- =====================================================================================================
-- V2026.10.060 · billing · Verificación de la conexión con el proveedor (pantalla "Conectar Factus").
-- Diseño: docs/fases/pantallas-faltantes-propuesta.md §2.
--
--   · credential_hint: usuario enmascarado (p. ej. ti***@correo.co) para reconocer las credenciales sin mostrarlas; nunca la clave.
--   · last_verified_at / last_verification_ok / last_verification_message: última verificación contra el proveedor de las
--     credenciales y el ambiente VIGENTES. Guardar credenciales nuevas o cambiar el ambiente las borra; encender la facturación
--     electrónica exige una verificación correcta (lo valida el dominio: BillingProviderSettings.Configure).
-- Las filas existentes quedan sin verificación: su modo actual no cambia; volver a encender después de apagar sí exige verificar.
-- =====================================================================================================

ALTER TABLE billing.provider_settings
    ADD COLUMN credential_hint            varchar(80),
    ADD COLUMN last_verified_at           timestamptz,
    ADD COLUMN last_verification_ok       boolean,
    ADD COLUMN last_verification_message  varchar(500),
    ADD CONSTRAINT ck_provider_settings__verification CHECK ((last_verified_at IS NULL) = (last_verification_ok IS NULL));

COMMENT ON COLUMN billing.provider_settings.credential_hint IS 'Usuario del proveedor enmascarado (ti***@correo.co). Nunca la clave ni el client secret.';
COMMENT ON COLUMN billing.provider_settings.last_verified_at IS 'Última verificación contra el proveedor de las credenciales y el ambiente vigentes (se borra al cambiarlos).';
COMMENT ON COLUMN billing.provider_settings.last_verification_ok IS 'Resultado de la última verificación; encender exige true.';
