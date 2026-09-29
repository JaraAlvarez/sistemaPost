# ADR-0016 · Sesiones con tokens opacos revocables (no JWT)

- **Estado:** Aceptada · 2026-09-28 · Fase 3

## Contexto
Los usuarios entran al backoffice (usuario + contraseña) y a la caja (código de cajero + PIN). Desactivar un usuario, revocar
un equipo o cambiar sus roles debe surtir efecto **de inmediato**, también sin Internet. Todo ocurre contra el servidor de
la tienda, que ya consulta la BD en cada petición.

## Decisión
- Token de sesión **opaco**: 256 bits aleatorios (`SecureTokens.Create`), enviado como `Authorization: Bearer`.
- En `identity.user_sessions` solo se guarda su **hash SHA-256**; el token nunca se registra en logs ni en la auditoría.
- Tipos `BACKOFFICE` y `TERMINAL`, con expiración **deslizante** por inactividad ⚙️ (`security.backoffice_idle_minutes` 30, `security.terminal_idle_minutes` 15) y máximo absoluto (`security.session_max_hours` 12).
- Cada petición lee la `security_version` vigente del usuario; los permisos efectivos se guardan en caché por esa versión,
  así que un cambio de roles, excepciones, contraseña o PIN se aplica en la siguiente petición. Desactivar el usuario
  revoca además todas sus sesiones.
- Revocación explícita (`/auth/logout`, `/identity/sessions/{id}/revoke`, desactivar usuario, revocar equipo).
- Las sesiones son estado **local del nodo** (no se sincronizan).
- JWT firmado se reserva para la licencia (Fase 12) y, eventualmente, el portal web.

## Consecuencias
- ✅ Revocación instantánea y sin listas negras; un robo de la BD no expone tokens utilizables.
- ✅ Simple: sin claves de firma que rotar en cada tienda.
- ⚠️ Una consulta por petición para validar la sesión (despreciable en LAN; se usa índice por `token_hash`).
- ⚠️ Una sesión solo vale en el nodo que la emitió; el portal en la nube tendrá su propio esquema.
