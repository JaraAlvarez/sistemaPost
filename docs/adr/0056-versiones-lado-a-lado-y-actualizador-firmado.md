# ADR-0056 · Versiones lado a lado y actualizador con manifiesto firmado

- **Estado:** Aceptada · 2026-09-30 · Fase 13 · Decisiones D13-03, D13-08 y D13-09 de la [propuesta](../fases/fase-13-propuesta.md)

## Decisión
1. **Lado a lado:** cada versión vive en `{app}\app\<versión>\{server,migrator,agent}`; `app\current` es una **unión NTFS** a la activa
   (los servicios apuntan a `app\current\…`) y `app\current.json` dice cuál es. Volver atrás = apuntar a la anterior. Se conservan ⚙️ 2.
2. **Manifiesto firmado** con Ed25519 y una **clave de actualizaciones propia** (distinta de la de licencias): `payload` (JSON en
   base64url) + `kid` + `signature` (`Pos.Updates.Contracts`). Trae producto, canal, versión, dirección, SHA-256 y tamaño del paquete,
   versión mínima de caja y novedades. Las claves de confianza van **embebidas** en el actualizador (`update-keys.json`). La herramienta
   `tools/Pos.Release` genera la clave, firma y verifica.
3. **Servicio `PosSupermercado-Updater`:** consulta ⚙️ cada 6 h el manifiesto del VPS (`https://<dominio>/updates/<canal>.json`, servido por
   Caddy), descarga y comprueba la huella, y aplica en la **ventana segura** (⚙️ 02:00, **sin jornadas abiertas**) o cuando el propietario
   pide "instalar ahora" (`POST /api/v1/system/updates/install-now`). Orden: backup `PRE_UPDATE` con el migrador actual → detener →
   migrar con el migrador nuevo (transacción) → cambiar de versión → arrancar → salud (`/health/ready`, 2 min). Si la migración falla, sigue
   la versión anterior sin cambios; si la versión nueva no arranca, **vuelve atrás sola**: versión anterior + restauración del backup previo.
4. El actualizador **no escribe en la BD**: deja `updates\history.jsonl` y `state.json`; el servidor audita el historial al arrancar
   (`UPDATE_DOWNLOADED`, `UPDATE_APPLIED`, `UPDATE_FAILED`, `UPDATE_ROLLED_BACK`, una sola vez) y muestra el estado en
   `GET /api/v1/system/updates` (permisos `system.update.view` y `.manage`).
5. **Cajas:** su actualizador (modo `Terminal`) lee el manifiesto y el paquete **del servidor de la tienda**
   (`/api/v1/system/updates/manifest` y `/package`, públicos porque van firmados), con la huella del certificado fijada; solo cambia el agente.

## Consecuencias
- Un corte de luz a mitad de la actualización deja la versión anterior o la nueva completa (el enlace y `current.json` se cambian al final).
- El actualizador se actualiza a sí mismo solo con el instalador (v1).
- No se audita en el servidor la actualización de cada caja (`TERMINAL_UPDATED` de la propuesta): queda en el registro de eventos de la caja.
