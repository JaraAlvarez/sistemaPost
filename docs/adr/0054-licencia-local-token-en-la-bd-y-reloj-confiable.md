# ADR-0054 · Licencia local: token en la BD, estado calculado, claves embebidas y reloj confiable

- **Estado:** Aceptada · 2026-09-29 · Fase 12-B · Decisiones D12B-01, D12B-04 a D12B-10 de la [propuesta](../fases/fase-12b-propuesta.md)
- **Complementa:** ADR-0038 (token Ed25519 con rotación por `kid`), ADR-0039 (edición y licencia por NIT), ADR-0048 (ancla externa).

## Decisión
1. **Token en la BD** (`licensing.license_state`, una fila por nodo). Viaja en los backups; restaurado en otro PC la huella no coincide y el
   estado es `REACTIVATION_REQUIRED`.
2. **El estado no se guarda**: `LicenseEvaluator` (función pura) lo calcula con el token firmado, la huella del equipo y el reloj:
   `DEMO` (30 días desde la creación de la instalación, "DEMOSTRACIÓN" en el tiquete), `VALID`, `VALID_OFFLINE` (sin check-in en 26 h),
   `GRACE`, `RESTRICTED` y `REACTIVATION_REQUIRED`. `last_state` existe solo para auditar los cambios (`LICENSE_STATE_CHANGED`).
3. **Claves públicas embebidas** en el binario (`trusted-keys.json`, recurso de `Pos.Modules.Licensing.Infrastructure`), que se completa al
   compilar la versión de producción con las claves `ACTIVE` y `STANDBY` del servidor. Fuera de producción se aceptan claves y una huella
   de desarrollo por configuración (`Pos:Licensing:DevelopmentTrustedKeys`, `DevelopmentFingerprint`); en producción se ignoran.
   **Cambio frente a la propuesta:** el contrato de 12-A no devuelve claves en el check-in, así que la lista no se actualiza en línea; la
   rotación por `kid` (la de reserva ya viaja embebida) cubre el cambio de clave sin publicar una versión.
4. **Reloj confiable (RN-LIC-05):** cada token aceptado guarda la diferencia entre su emisión (`iat`, firmada) y el reloj local
   (`clock_offset_seconds`, ignorada si es menor de 5 minutos) y la hora del último check-in en hora confiable. La mayor hora observada
   sube cada minuto. Si el reloj corregido queda más de 24 h por detrás, el estado es `RESTRICTED` y se audita `LICENSE_CLOCK_ROLLBACK`;
   el siguiente check-in exitoso lo corrige.
5. **Check-in** cada ⚙️ 24 h ± 2 h, al arrancar y con reintentos (1 min → 1 h). Envía las jornadas abiertas y el **sello de auditoría**
   (`CheckinAuditSeal`, campo opcional del contrato; la nube lo guarda en `checkins`, migración de la nube V005, y el portal lo muestra).
6. Solo el propietario activa y libera (`licensing.license.manage`); el administrador ve y verifica.

## Consecuencias
- Editar la BD no sirve: el token va firmado y el estado se recalcula.
- Un intento de activación rechazado no queda en `licensing.checkins` (se revierte con su transacción); sí queda el rechazo en la nube.
- Antes de vender, el propietario debe copiar sus claves públicas a `trusted-keys.json` y compilar (ver [despliegue-nube.md](../despliegue-nube.md) §15).
