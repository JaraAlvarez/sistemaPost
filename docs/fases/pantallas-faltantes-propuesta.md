# Pantallas de administración faltantes — propuesta de diseño

Rama `pantallas-admin`. Dos pantallas que el servidor ya soportaba casi por completo pero que solo se podían manejar por API o por
archivo de configuración. Se reutiliza lo existente; lo nuevo es mínimo y queda listado aquí. Ronda 2: salvaguardas de la cola (PA1),
adenda del ADR-0059 (PA2) y endurecimiento de la comprobación de carpetas y de la verificación (PA3–PA9).

## 1. Destino de los backups (Configuración → Backups)

**Cómo funciona hoy (Fase 11, se respeta):** cada backup (SCHEDULED, NIGHTLY, CASH_CLOSING, MANUAL) se crea siempre en el destino
**LOCAL** fijo `{DataRoot}\backups\local` y desde ahí se copia a los demás destinos activos (`EXTERNAL` = disco por letra y etiqueta
opcional, `NETWORK` = carpeta compartida UNC, `S3`). El backup `PRE_UPDATE` lo hace el migrador en `{DataRoot}\updates\backups` y la
restauración de prueba lee la carpeta LOCAL. Por eso **la carpeta LOCAL no se mueve**: "cambiar dónde se guardan los respaldos
automáticos" es agregar o cambiar destinos EXTERNAL/NETWORK, que reciben una copia de cada backup automático con su propia retención.

| Pieza | Estado |
|---|---|
| Destinos, retención, "Probar", "Respaldar ahora", auditoría `BACKUP_DESTINATION_CHANGED`, permiso `backup.destination.configure` (sensible) | Ya existía |
| `GET /backups/overview`: carpeta LOCAL, si los backups están configurados, último backup y último correcto | Nuevo (permiso `backup.backup.view`) |
| `POST /backups/destinations/check-folder`: comprueba una carpeta ANTES de guardar | Nuevo (permiso `backup.destination.configure`) |
| Al guardar un destino EXTERNAL/NETWORK activo nuevo, con carpeta o etiqueta cambiada o reactivado, la comprobación debe pasar (`BACKUP.FOLDER_INVALID`). Corre como consulta ANTES del comando, fuera de su transacción, en un hilo aparte con tiempo máximo de 10 s (PA5) | Nuevo |
| Pantalla: pestaña **Backups** en Configuración (`DestinoBackups.razor`) | Nuevo |

**Reglas de la comprobación** (`BackupFolderProbe`, corre en el servidor con la cuenta del servicio):

- Ruta completa; se rechazan las rutas de dispositivo `\\?\` y `\\.\`.
- **Disco externo (PA4):** letra de unidad, nunca UNC; unidad `Removable` o `Fixed`; en un **volumen distinto** del de la carpeta LOCAL
  (se compara el número de serie del volumen, que también descubre letras de `subst`). Solo fuera de producción se puede permitir el mismo
  volumen (`Pos:Backup:AllowSameVolumeDestinations`, para las pruebas).
- **Carpeta de red (PA3):** UNC `\\equipo\carpeta` de OTRO equipo: se rechazan `localhost`, `127.x`, `::1` (también
  `0--1.ipv6-literal.net`) y el nombre de este equipo (con o sin dominio).
- **Alias de la LOCAL:** además de comparar rutas, el archivo de prueba se busca dentro de la carpeta LOCAL: si aparece, la carpeta es la
  LOCAL o está dentro de ella (junction, `subst`, `\\este-equipo\recurso`).
- **Sin crear nada (PA7):** debe existir la carpeta o, si no, su carpeta superior (la de destino se crea en el primer backup). Se escribe,
  lee y borra un archivo temporal; no se crean carpetas.
- **Cuenta (PA8):** el servicio corre como LocalSystem; en la red entra con la **cuenta del equipo** (`EQUIPO$`). Hay que darle permiso
  de escritura en la carpeta compartida; en un NAS o equipo **sin dominio** esa cuenta no existe y la copia puede fallar (la pantalla lo
  advierte y sugiere un disco externo).

Un disco externo desconectado después no es error: la copia queda pendiente como hasta hoy.

## 2. Conectar Factus desde la interfaz (Facturación → Configuración)

**Cómo funcionaba:** el adaptador lo fijaba `Pos:Billing:Provider` en `server.json` (`NONE` por defecto); modo, ambiente y credenciales
(cifradas con DPAPI, nunca se devuelven) ya se guardaban por API; encender exigía credenciales pero no que funcionaran.

**Decisión — Propuesta, pendiente de aprobación del usuario (adenda A del ADR-0059):** el adaptador por defecto pasaría a ser **FACTUS**.
**Ronda 3 (PB2): mientras no se apruebe, el código mantiene `NONE` por defecto** (`BillingOptions.DefaultProvider`, una sola línea para
cambiarlo) y la tarjeta Factus explica cómo habilitarlo en `server.json`. Lo
que apaga la facturación electrónica es el modo `OFF` (por defecto, en la BD). `NONE` sigue para prohibirla en una instalación y `FAKE`
para desarrollo. Como ahora el adaptador real está siempre presente, la cola tiene salvaguardas (PA1):

1. **La cola envía SOLO con modo ≠ OFF y la conexión verificada** (`FiscalConnectionFactory.GetForSendingAsync`, `CanSend`). Con OFF no
   sale nada, ni lo que ya estaba en cola. Los rangos (consulta) siguen pudiendo sincronizarse con solo tener llaves.
2. **Migración V2026.10.060:** las filas con modo ≠ OFF (encendidas sin verificación: no puede haber verificación previa a esta versión)
   pasan a `OFF` con `auto_disabled_at`; el **Tablero** y la tarjeta Factus avisan "La facturación electrónica quedó apagada: conéctela en
   Facturación → Configuración" hasta que se vuelva a encender.
3. **Atraso retenido:** la primera verificación correcta fija `backlog_cutoff`. Los documentos electrónicos pendientes, con error o en
   contingencia emitidos ANTES no se envían solos. La tarjeta muestra "N documentos pendientes de antes de conectar" con **Enviar ahora**
   (`POST /billing/backlog/send`, auditado `FISCAL_BACKLOG_RELEASED`) y **No enviar** (`POST /billing/backlog/discard`: estado
   `NOT_SENT`, que no se reintenta, auditado `FISCAL_BACKLOG_DISCARDED`). Permiso `billing.settings.manage`.

| Pieza | Estado |
|---|---|
| Credenciales cifradas (`PUT /billing/settings/credentials`), modo y ambiente (`PUT /billing/settings`), rangos | Ya existía |
| `POST /billing/settings/verify`: verifica las llaves guardadas con el token de Factus (`oauth/token`); registra y audita `FISCAL_CONNECTION_VERIFIED` | Nuevo |
| `POST /billing/settings/verify-candidate`: prueba llaves candidatas SIN guardarlas ni devolverlas (PA6) | Nuevo |
| `POST /billing/settings/credentials/rotate`: "Cambiar llaves": prueba las nuevas y solo si pasan las guarda, sin apagar; si fallan no toca nada (PA6) | Nuevo |
| Guardar llaves por la vía de "Conectar" apaga el modo y borra la verificación; cambiar el ambiente también la borra; encender exige verificación correcta (`BILLING.VERIFICATION_REQUIRED`) | Nuevo |
| **PA9:** un fallo transitorio (sin red, tiempo agotado, Factus caído) no quita una verificación correcta; solo el rechazo de las llaves la quita. Máximo una verificación por empresa cada 10 s (`BILLING.VERIFY_TOO_SOON`, `Pos:Billing:VerifyMinSeconds`). Verificar reutiliza el token vigente de esas llaves: no reinicia el del trabajador de la cola | Nuevo |
| `billing.provider_settings`: `credential_hint`, `last_verified_at`, `last_verification_ok`, `last_verification_message`, `backlog_cutoff`, `backlog_released`, `auto_disabled_at`; `fiscal_documents.status` acepta `NOT_SENT` — migración **V2026.10.060** (no desplegada; corregida en la ronda 2) | Nuevo |
| Pantalla: tarjeta `FactusConexion.razor` con el flujo de `SistecreditoTarjeta.razor` | Nuevo |

**Flujo "Conectar":** (1) si se escribieron llaves, se guardan cifradas con la facturación apagada; (2) se guarda el ambiente con modo
`OFF`; (3) se verifica contra Factus; (4) solo si verificó, se sincronizan los rangos y se enciende el modo elegido. **Conectado** muestra
ambiente, modo, usuario enmascarado y última prueba, con **Volver a probar**, **Cambiar llaves** (prueba antes de guardar; con las casillas
vacías no hace nada) y **Desconectar** (modo `OFF`; las llaves quedan). Para cambiar de ambiente: Desconectar y Conectar. Ningún comando
nuevo queda permitido con la licencia restringida (R10 sin cambios).

### Ronda 3

| Punto | Qué se hizo |
|---|---|
| PB1 | `fiscal_documents.environment`: ambiente con que se emitió cada documento electrónico (las notas heredan el de su factura). La cola solo envía los del ambiente vigente; verificar en otro ambiente o con otras llaves mueve el corte del atraso (`backlog_environment`, `backlog_credentials_at`). Lo de otro ambiente queda retenido, visible y solo con "No enviar" |
| PB2b | "Volver a probar" valida la clave con un token nuevo (password grant) en un espacio de token aparte del de la cola; la tarjeta muestra "Clave validada: fecha y hora" |
| PB3 | Factus no recibe fecha de emisión (`FactusRequests` no la tiene; supuesto S10 de la guía): "Enviar ahora" emite con la fecha de hoy; la pantalla y la guía lo dicen, con el aviso del plazo DIAN y de posibles rechazos |
| PB4 | Columna "No enviadas" (`NotSent`) en la conciliación |
| PB5 | "Enviar ahora" confirma con el número de documentos; "No enviar" confirma dos veces y advierte que es irreversible |
| PB6 | Una nota crédito o de ajuste cuyo documento quedó NOT_SENT o cancelado queda NOT_SENT ("no aplica") con alerta `FISCAL_DOCUMENT_NOT_SENT`; las notas nuevas sobre una factura NOT_SENT son internas |
| PB7 | El atraso cuenta y descarta también lo reclamado (SUBMITTING); lo retenido no infla "pendientes vencidos" |
| PB8 | Verificar, probar candidatas y cambiar llaves son `ISelfTransactedCommand`: Factus se llama fuera de la transacción y se guarda en transacciones cortas, solo si las llaves y el ambiente no cambiaron mientras tanto |
| PB9 | Una comprobación de carpeta a la vez por destino: si una sigue colgada se espera esa misma (no se abren hilos nuevos); el probe recibe un token que vence a los 10 s |
| PB10/PB11 | El volumen se identifica con `GetFinalPathNameByHandle` (GUID del volumen) por `[DllImport]`, sin código inseguro; si no se puede identificar, se rechaza con mensaje claro |
| PA3/PA4/PA8 | Red: solo IP privadas (RFC 1918, IPv6 ULA) o nombres que resuelvan solo a ellas; se rechazan 0.0.0.0, loopback, las IP de todas las interfaces de este equipo, su nombre y su FQDN; aviso de seguridad en la pantalla. Ruta real (junctions, puntos de montaje, subst) comparada con la LOCAL; sin `EnumerateFiles` |
| PA6 | El freno de 10 s es por juego de llaves: otras llaves candidatas no esperan |
| R10 | Con licencia RESTRINGIDA: `ReleaseFiscalBacklogCommand`, `VerifyBillingConnectionCommand` y `RetryFiscalDocumentCommand` permitidos; `DiscardFiscalBacklogCommand` y `RotateProviderCredentialsCommand`, no |

## 3. Pruebas

- Unitarias: reglas de `BillingProviderSettings` (verificación, `CanSend`, atraso, falla transitoria, cambio de llaves), `NOT_SENT`, el
  simulado y `VerifyAsync` de Factus (token reutilizado); `BackupFolderProbe` (rutas de dispositivo, LOCAL y su alias por junction, sin
  crear carpetas, mismo volumen, UNC propio).
- BD: restricciones de la V060 y migración de una fila encendida sin verificación (queda OFF con aviso).
- Integración (`PantallasAdmin`): conexión Factus contra el Factus simulado (guardar, verificar, activar, sin Internet, cambiar llaves,
  desconectar, freno de 10 s, permisos, auditoría); cola segura (OFF con llaves no envía; instalación vieja encendida no envía; atraso
  "Enviar ahora" / "No enviar"); destinos de backup.
