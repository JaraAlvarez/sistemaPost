# Pantallas de administración faltantes — propuesta de diseño

Rama `pantallas-admin`. Dos pantallas que el servidor ya soportaba casi por completo pero que solo se podían manejar por API o por
archivo de configuración. Se reutiliza lo existente; lo nuevo es mínimo y queda listado aquí.

## 1. Destino de los backups (Configuración → Backups)

**Cómo funciona hoy (Fase 11, se respeta):** cada backup (SCHEDULED, NIGHTLY, CASH_CLOSING, MANUAL) se crea siempre en el destino
**LOCAL** fijo `{DataRoot}\backups\local` y desde ahí se copia a los demás destinos activos (`EXTERNAL` = disco USB por letra y
etiqueta opcional, `NETWORK` = carpeta compartida UNC, `S3`). El backup `PRE_UPDATE` lo hace el migrador en
`{DataRoot}\updates\backups` y la restauración de prueba lee la carpeta LOCAL. Por eso **la carpeta LOCAL no se mueve**: mover la carpeta de
trabajo rompería el actualizador, la restauración de prueba y la descarga. "Cambiar dónde se guardan los respaldos automáticos" es
agregar o cambiar los destinos EXTERNAL/NETWORK, que reciben una copia de cada backup automático (con su propia retención).

| Pieza | Estado |
|---|---|
| Destinos, retención, "Probar", "Respaldar ahora", auditoría `BACKUP_DESTINATION_CHANGED`, permiso `backup.destination.configure` (sensible) | Ya existía |
| `GET /backups/overview`: carpeta LOCAL, si los backups están configurados en el equipo, último backup y último correcto | Nuevo (consulta, permiso `backup.backup.view`) |
| `POST /backups/destinations/check-folder`: comprueba una carpeta ANTES de guardar (ruta completa, unidad conectada y con la etiqueta pedida, carpeta compartida accesible, escribir/leer/borrar un archivo, espacio libre) | Nuevo (consulta sin efectos que perduren, permiso `backup.destination.configure`) |
| Guardar un destino EXTERNAL/NETWORK **activo** exige que la comprobación pase (`BACKUP.FOLDER_INVALID`); `NETWORK` exige ruta UNC (`\\equipo\carpeta`: el servicio no ve las unidades mapeadas) y ninguna carpeta puede ser la LOCAL ni estar dentro de ella | Nuevo |
| Pantalla: pestaña **Backups** en Configuración (componente `DestinoBackups.razor`) | Nuevo |

La comprobación corre en el **servidor** con la cuenta del servicio (la misma que copia los backups), no en el navegador. Si la carpeta no
existe, se crea para probar y se borra si quedó vacía. Un disco externo desconectado después no es error: la copia queda pendiente como
hasta hoy.

## 2. Conectar Factus desde la interfaz (Facturación → Configuración)

**Cómo funciona hoy:** el adaptador lo fija `Pos:Billing:Provider` en `server.json` (`NONE` por defecto); modo, ambiente y credenciales
(cifradas con DPAPI, nunca se devuelven) ya se guardaban por API; encender exigía credenciales pero no que funcionaran.

**Decisión (cambia ADR-0059 §3):** el adaptador por defecto pasa a ser **FACTUS**. Lo que apaga la facturación electrónica es el modo
`OFF` (por defecto, en la BD), no el archivo. `NONE` sigue disponible para prohibirla en una instalación y `FAKE` para desarrollo. Con modo
`OFF` no se llama nunca a Factus, así que las instalaciones existentes no cambian de comportamiento.

| Pieza | Estado |
|---|---|
| Credenciales cifradas (`PUT /billing/settings/credentials`), modo y ambiente (`PUT /billing/settings`), rangos (`/billing/ranges`, `/ranges/sync`) | Ya existía |
| `POST /billing/settings/verify`: verifica contra Factus pidiendo un token (`oauth/token`, lo único que ya llamaba el cliente para probar credenciales); registra el resultado y lo audita (`FISCAL_CONNECTION_VERIFIED`) | Nuevo |
| `IFiscalProvider.VerifyAsync`: Factus → `AuthenticateAsync`; simulado → correcto salvo `Offline`/`RejectCredentials`; nulo → "no configurado" | Nuevo |
| Guardar credenciales nuevas **apaga** el modo y borra la verificación ("guardar desactivado"); cambiar el ambiente también borra la verificación | Nuevo |
| Encender (OFF → ON_REQUEST/EVERY_SALE, o cambiar el ambiente estando encendida) exige la última verificación correcta: `BILLING.VERIFICATION_REQUIRED` | Nuevo |
| `billing.provider_settings`: `last_verified_at`, `last_verification_ok`, `last_verification_message`, `credential_hint` (usuario enmascarado, p. ej. `ti***@correo.co`) — migración **V2026.10.060** | Nuevo |
| Pantalla: tarjeta `FactusConexion.razor` con el flujo de `SistecreditoTarjeta.razor` | Nuevo |

**Flujo "Conectar":** (1) si se escribieron llaves, se guardan (quedan cifradas y la facturación apagada); (2) se guarda ambiente con modo
`OFF`; (3) se verifica contra Factus; (4) solo si verificó, se sincronizan los rangos (resolución, prefijo, desde/hasta, vigencia) y se
enciende el modo elegido. **Conectado** muestra ambiente, modo, usuario enmascarado y última prueba, con **Volver a probar**, **Cambiar
llaves** (repite el flujo) y **Desconectar** (modo `OFF`; las llaves quedan guardadas). La asignación de rangos a sucursales sigue en la
pestaña Rangos. Permiso: `billing.settings.manage` (sensible, dueño y administrador). Ningún comando nuevo queda permitido con la licencia
restringida (R10 sin cambios).

## 3. Pruebas

- Unitarias: reglas de verificación de `BillingProviderSettings`; `VerifyAsync` del adaptador Factus contra el Factus simulado y del
  simulado; `BackupFolderProbe` (ruta relativa, carpeta LOCAL, archivo en lugar de carpeta, carpeta nueva que se limpia, escritura).
- Integración: flujo Factus completo contra el Factus simulado en Kestrel (guardar → verificar → activar → volver a probar → cambiar
  llaves → desconectar, permisos y auditoría) y destinos de backup (comprobación, rechazo al guardar, auditoría y permisos).
