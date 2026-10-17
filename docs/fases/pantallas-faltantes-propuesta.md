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

### Ronda 4

| Punto | Qué se hizo |
|---|---|
| N2 | `fiscal_numbering_ranges.environment`: cada ambiente tiene sus rangos (llave única y asignación por ambiente). La sincronización, la lista, la asignación (`BILLING.RANGE_OTHER_ENVIRONMENT`), las alertas y la selección del rango usan solo el ambiente vigente. `provider_settings.ranges_environment`: la cola no envía hasta sincronizar los rangos del ambiente vigente (la tarjeta lo avisa con "Sincronizar rangos"; el trabajador sincroniza solo) |
| N1 | Rotar llaves en el MISMO ambiente no mueve el corte (solo el cambio de ambiente). "No enviar" excluye lo ya intentado (`attempts > 0`) o reclamado: queda **en revisión** ("consulte el estado en Factus") y se cuenta aparte (`HeldInReview`) |
| N3 | Si la carpeta LOCAL aún no existe, la ruta real y el disco se resuelven desde su ancestro existente más cercano |
| N4 | Además del freno por llaves, máximo 5 verificaciones por minuto por empresa; el token de la verificación no se guarda en memoria |
| N5 | Antes de cada copia a una carpeta de red se vuelve a exigir un equipo de la red local (el nombre pudo cambiar de dirección) |
| N6 | `Lazy` en `GetOrAdd` y `TryRemove(KeyValuePair)` en las comprobaciones de carpeta en curso |
| N7 | El procesador compara el ambiente del documento con el de la conexión antes de reclamarlo (segunda defensa) |
| N8 | El vencimiento del crédito se cuenta desde la fecha real de emisión (la del envío, `FiscalHeader.SubmittedOn`), con los mismos días de plazo |
| PB6 | Una nota sin rango de numeración por más de 72 h deja de reintentarse (error sin próximo intento) con alerta crítica `FISCAL_RANGE_MISSING`; se reintenta a mano |
| PB10 | Si no se puede resolver la ruta real de la carpeta o de la LOCAL, se rechaza con un mensaje claro |

**Nota de despliegue:** la V2026.10.060 cambió de checksum en cada ronda. Una BD de **desarrollo** que ya aplicó una versión anterior se
recrea (`dev-db.ps1`) o se borra su fila en `system.schema_migrations` (y lo que creó) antes de migrar. En producción no hay ninguna
(ver `docs/notas-de-version.md`).

## 3. Pruebas

- Unitarias: reglas de `BillingProviderSettings` (verificación, `CanSend`, atraso, falla transitoria, cambio de llaves), `NOT_SENT`, el
  simulado y `VerifyAsync` de Factus (token reutilizado); `BackupFolderProbe` (rutas de dispositivo, LOCAL y su alias por junction, sin
  crear carpetas, mismo volumen, UNC propio).
- BD: restricciones de la V060 y migración de una fila encendida sin verificación (queda OFF con aviso).
- Integración (`PantallasAdmin`): conexión Factus contra el Factus simulado (guardar, verificar, activar, sin Internet, cambiar llaves,
  desconectar, freno de 10 s, permisos, auditoría); cola segura (OFF con llaves no envía; instalación vieja encendida no envía; atraso
  "Enviar ahora" / "No enviar"); destinos de backup.

---


## Usuarios: detalle, roles, PIN y contraseña

> Rama `pantalla-usuarios`. Origen: en Administración → Usuarios solo había Nuevo, Desbloquear, Inactivar y Activar; con el instalador
> premium el Propietario se crea sin código de caja y no tenía cómo ponerse un PIN.

### Lo que ya existía en el servidor (se reutiliza)

| Endpoint | Permiso | Uso en la pantalla |
|---|---|---|
| `PUT identity/users/{id}` | `identity.user.manage` | Editar nombre y correo (se conserva el empleado vinculado) |
| `PUT identity/users/{id}/roles` | `identity.role.manage` | Roles del usuario (no a sí mismo, RN-SEC-05 sin escalar permisos) |
| `POST identity/users/{id}/reset-pin` | `identity.user.manage` | Código de caja + PIN nuevo de otro usuario |
| `POST identity/users/{id}/reset-password` | `identity.user.manage` | Contraseña temporal; obliga a cambiarla y cierra sus sesiones |
| `POST auth/change-pin` | sesión | Cambiar el propio PIN (pedía tener ya código de caja) |
| `GET identity/roles` | `identity.permission.view` **o** `identity.role.manage` (PU10) | Catálogo de roles con su descripción |

### Decisiones

| # | Decisión | Motivo |
|---|---|---|
| DU-01 | Diálogo de **detalle del usuario** desde la tabla: datos (nombre, correo), roles globales como casillas con su descripción, código de caja y estado del PIN. Cada bloque se guarda con su propio endpoint y solo aparece si la sesión tiene el permiso que pide el servidor | Mismo permiso en la interfaz y en el servidor |
| DU-02 | Los roles asignados **por sucursal** se muestran como "solo en una sucursal" y se reenvían sin cambios al guardar | `PUT roles` reemplaza todas las asignaciones; la pantalla no los gestiona todavía |
| DU-03 | `POST auth/change-pin` acepta además `posCode` y `currentPassword`: si el usuario **no tiene PIN**, lo define confirmando su contraseña y, si tampoco tiene código de caja, eligiéndolo (3 a 6 dígitos, único). Con PIN ya existente se exige el PIN actual y el código no se cambia por esta vía | El Propietario premium (y cualquier usuario sin PIN) puede ponerse uno sin depender de otro administrador; la contraseña evita que una sesión olvidada abierta defina un PIN |
| DU-04 | Nuevo `GET auth/credentials`: código de caja, si tiene PIN y las longitudes configuradas (`security.pin_length`, `security.password_min_length`). Si falla, la pantalla usa las de fábrica (PU8) | La pantalla muestra la regla exacta sin pedir permiso de parámetros |
| DU-05 | `reset-pin` sobre uno mismo se rechaza (`IDENTITY.USE_CHANGE_PIN`), igual que ya hacía `reset-password` | El propio PIN se cambia conociendo el actual o la contraseña |
| DU-06 | **Jerarquía** (`IDENTITY.TARGET_OUTRANKS`): solo se gestiona a quien no tiene más poder que uno. El poder del destino son los permisos de sus **roles** más sus excepciones GRANT, en cada alcance, **sin restar sus DENY** (PU1). Se aplica a editar (incluido el correo), roles, excepciones, activar/inactivar, desbloquear, `reset-pin` y `reset-password`. En excepciones, quitar un DENY cuenta como conceder ese permiso | Un DENY es reversible: si contara, un Administrador pondría DENY al Propietario, le restablecería la contraseña y quitaría el DENY |
| DU-07 | Nunca queda la empresa **sin Propietario activo** (`IDENTITY.LAST_OWNER`): ni al quitarle el rol ni al inactivarlo, aunque queden Administradores. Antes de contar Propietarios o Administradores se toma `pg_advisory_xact_lock` por empresa dentro de la transacción (PU4) | Complementa RN-SEC-04; dos Propietarios que se degradan a la vez no dejan la empresa sin ninguno |
| DU-08 | Auditoría: la que ya hace el servidor (`PIN_RESET`, `PASSWORD_RESET`, `PIN_CHANGED`, cambios de la entidad Usuario). Definir el PIN por primera vez se audita como `PIN_CHANGED` con el texto "definió su código de caja y su PIN" | Sin acciones nuevas en el catálogo |
| DU-09 | Menú del usuario: «Cambiar PIN de caja» junto a «Cambiar contraseña», con la página `/cambiar-pin` | Coherente con la página de contraseña |
| DU-10 | La contraseña o el PIN actuales en `change-password` y `change-pin` pasan por el mismo contador, bloqueo y auditoría que el ingreso (`LOGIN_FAILED`, `USER_LOCKED`); el intento fallido se confirma y se responde `IDENTITY.CURRENT_*_INVALID` o `IDENTITY.CREDENTIAL_LOCKED` (no 401: la sesión sigue). Ambos endpoints usan el límite de peticiones `auth-login` (PU5). Un cambio exitoso reinicia los intentos fallidos. `/cambiar-clave` muestra la longitud mínima y el historial reales (`GET auth/credentials`); el asistente web de instalación usa el mínimo de fábrica (8), porque la empresa aún no tiene parámetros | Sin esto, una sesión abierta permite adivinar la contraseña o el PIN sin límite |
| DU-11 | `reset-pin` y `change-pin` cierran las sesiones de **caja** abiertas del usuario (salvo la actual) (PU6) | El PIN anterior deja de servir de inmediato |
| DU-12 | El PIN tampoco puede ser igual al código de caja ni uno de una lista corta de PINs comunes (1212, 2580, 1122…) (PU7) | Son los primeros que se prueban |
| DU-13 | `ResetPinCommand` y `ResetPasswordCommand` se permiten con la licencia restringida (R10) (PU11) | La tienda debe poder recuperar el acceso |
| DU-14 | La contraseña temporal sugerida usa `RandomNumberGenerator` y respeta la longitud mínima de la empresa (PU9) | Imprevisible y válida a la primera |
| DU-15 | Editar o borrar un rol personalizado exige tener **todos** sus permisos actuales y poder gestionar a cada usuario que lo tiene (UR1) | Quitarle permisos al rol de un superior, o borrarlo, lo degradaría |
| DU-16 | Para el alcance "todas las sucursales", los permisos propios son los que se tienen en **cada** sucursal: se restan los que niega un DENY por sucursal de quien actúa. Vale para la jerarquía y para conceder roles o excepciones (UR2) | Un DENY en una sucursal no se esquiva concediendo o gestionando en global |
| DU-17 | Cerrar la sesión de otro aplica la jerarquía; con autorización de supervisor, se compara con el supervisor que autorizó (UR3) | Un supervisor no cierra la sesión del Propietario |
| DU-18 | `GET auth/credentials` funciona con la contraseña temporal pendiente (UR4) y `SetUserOverrides` toma el mismo candado cuando el objetivo es Propietario o Administrador (UR5) | La pantalla de contraseña temporal muestra la longitud real; los cambios que cuentan administradores se serializan |
| DU-19 | Revocar o re-emparejar un **equipo** cierra todas sus sesiones, también las de un superior: es por diseño (protege la tienda ante un equipo perdido o reemplazado) y no aplica la jerarquía. La auditoría (`USER_SESSIONS_REVOKED` sobre el equipo) dice a quiénes se les cerró | Seguridad del equipo antes que la jerarquía |
| DU-20 | Un **Propietario activo** no pierde nada por sus propios DENY en la jerarquía ni al conceder, y puede **quitarse** sus propios DENY (excepción a "no a sí mismo", solo para quitar). Al último Propietario o Administrador no se le puede poner DENY de `identity.role.manage`, `identity.user.manage` ni `identity.session.revoke` (`IDENTITY.LAST_ADMINISTRATOR_LOCKOUT`). En excepciones, el candado de administradores se toma antes de verificar | Un DENY heredado no deja sin salida al único Propietario |
| DU-21 | Si quien cierra una sesión actúa por autorización de supervisor, el rechazo por jerarquía es genérico ("El supervisor que autorizó no puede cerrar la sesión de esa persona"), sin listar permisos | Lo lee el cajero en la caja |

### Nota de versión: roles personalizados antiguos y jerarquía

Los roles **de sistema** reciben los permisos nuevos de cada versión; las **copias personalizadas** (p. ej. un "Gerente" clonado de
Supervisor de caja antes de la versión con báscula) no. Con la jerarquía, quien tiene esa copia ya no puede gestionar a los Supervisores
de caja, que sí tienen los permisos nuevos. El error `IDENTITY.TARGET_OUTRANKS` lista los permisos que faltan (sus descripciones del
catálogo; quien gestiona usuarios ya ve los roles de cada uno), y la pantalla Usuarios agrega la causa y la salida: pedir al
Propietario que agregue esos permisos al rol personalizado.

Sin migraciones: el modelo ya tiene `pos_code`, `pin_hash` y `must_change_password`.

### Pruebas
- Unitarias (dominio): elección del código de caja propio, regla del último Propietario, PINs comunes e iguales al código.
- Integración: PIN propio por primera vez (con contraseña, código único), cambio con PIN actual, `reset-pin` propio rechazado,
  jerarquía (gerente frente al Propietario en todas las acciones), cadena DENY + reset rechazada, quitar DENY como concesión,
  Propietario que degrada a otro, degradación simultánea de dos Propietarios (concurrencia), bloqueo al fallar la contraseña o el
  PIN actuales y cierre de sesiones de caja.
