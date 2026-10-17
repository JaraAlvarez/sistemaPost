# Endurecimiento de sincronización y pagos en línea (revisión R6, S1–S3, W1–W3, R4)

Diseño breve de las correcciones pedidas por la revisión de seguridad. Rama `fix-sync-pagos`. Migraciones: tienda `V2026.10.100` a
`V2026.10.102`; nube `V2026.10.013` (claves de firma), `V2026.10.014` (pagos) y `V2026.10.015` (limpieza de datos ya recibidos). La
`V2026.10.012` de la nube es de main (`owner_link_request_code`).

**Orden de despliegue: primero la nube, después las tiendas** (despliegue-nube.md §10 y §17).

## 1. Sincronización (tienda → nube)

### R6 · Outbox SYNC sin secretos ni datos personales en claro

- `PosSaveChangesInterceptor.SyncValues` excluye las propiedades `[Sensitive]` (hash de contraseña, de PIN, credencial del equipo):
  no se escriben ni enmascaradas. El cambio de una propiedad sensible sigue subiendo el `row_version` (es un cambio del maestro).
  Se elige excluir `[Sensitive]` en vez de marcar `PasswordHash`/`PinHash` como `[LocalOnly]` porque `[LocalOnly]` además deja de
  subir la versión y de auditar, y el módulo Identity no se toca en esta rama.
- Los `[PersonalData]` se enmascaran igual que en la auditoría (`MaskPersonal`: correo `j***@dominio`, teléfono `***1234`, texto libre
  `(registrado)`). Hoy NADIE consume los mensajes `SYNC` (el procesador solo toma `LOCAL`; la nube recibe ventas, cierres, existencias
  y productos leyendo las tablas, no el outbox): la nube no necesita esos datos. Un consumidor futuro debe leer el valor vigente de
  la tabla, no del mensaje.
- Migración `V2026.10.100__system__redact_sync_outbox.sql`: limpia los mensajes `sync.entity_changed.v1` ya guardados (quita
  `password_hash`, `pin_hash`, `credential_hash` y enmascara `email`, `phone`, `address`, `notes` de `identity.users` y
  `parties.parties`). No borra mensajes: el diseño D4-09 los reserva para la sincronización de maestros; su purga queda pendiente
  hasta que exista el consumidor.
- Supresión del titular (ronda 2): `AnonymizeCustomerHandler` llama a `ISyncOutboxRedaction`, que reemplaza por `(suprimido)` TODOS
  los valores de los mensajes SYNC ya guardados de `parties.parties` y `customers.customers` con ese id (nombre, identificación,
  `search_text`, contacto: no todos están marcados `[PersonalData]`) y los marca `"redacted": true`. El cambio de la propia supresión
  se escribe después, ya anonimizado.

### S1 · Ventas sin cédula/NIT y supresión propagada

- El tipo `SALE` deja de subir `customerIdentification` (la nube no la usa).
- `sales.sales.customer_redacted_at` (nueva columna): la supresión del titular (`AnonymizeCustomerHandler`) la marca en todas sus
  ventas por el puerto `ICustomerSaleRedaction` de `Sales.Contracts`. La copia LOCAL de la venta se conserva (retención legal), pero el
  feed sube `customer = null` cuando está marcada, y la versión pasa a `GREATEST(completed_at, voided_at, customer_redacted_at)`:
  la venta vuelve a subir con versión nueva y la nube la reemplaza anonimizada. `GREATEST(completed_at, voided_at)` es igual a la
  expresión anterior `COALESCE(voided_at, completed_at)`, así que los cursores existentes siguen siendo válidos.
- `STOCK` deja de subir `averageCost` (el portal solo muestra cantidad y valor total).
- Ronda 2: `V2026.10.102` (tienda) marca las ventas de los clientes que ya estaban anonimizados, para que se reenvíen sin el nombre;
  `V2026.10.015` (nube) quita `customerIdentification` y `averageCost` de lo ya recibido (`jsonb -`).
- «Ventas recientes» del portal se muestra y ordena por `completedAt` de la venta (no por la versión, que cambia con una anulación o una
  supresión).

### S2 · Paquete .possync firmado por la tienda

- Cada instalación genera un par Ed25519 (`{DataRoot}\config\sync-signing.key`, protegido con DPAPI de la máquina como la clave de
  los backups; fuera de Windows queda en claro con permisos del sistema de archivos, ver §4).
- Formato `POSSYNC2`: encabezado con `signer` (clave pública) + cifrado (igual que v1) + 64 bytes de firma Ed25519 sobre todo lo
  anterior. `POSSYNC1` (sin firma) se sigue leyendo.
- Registro de la clave en la nube: los módulos de licencias de la tienda no se tocan en esta rama, así que el registro va por el
  mismo canal autenticado que ya usa la sincronización en línea (token de licencia + huella, D16-03): cada lote en línea lleva
  `X-Pos-Sync-Key` y una prueba de posesión `X-Pos-Sync-Key-Proof` (firma de `pos-sync-key-v1:<instalación>:<lote>`). La nube la
  guarda en `sync.installation_keys` (o la rota, con auditoría en el registro de la tabla).
- **Pendiente por diseño:** registrar la clave en la ACTIVACIÓN o el CHECK-IN exige tocar el módulo de licencias de la tienda, en el que
  trabaja otra rama. Queda para después; mientras tanto el registro va por la sincronización en línea, que se autentica igual.
- Rotación (ronda 2): si llega una clave distinta se audita `SYNC_KEY_ROTATED` (la primera, `SYNC_KEY_REGISTERED`) y la anterior
  (`previous_key`) se acepta 7 días más (paquetes exportados antes de rotar). Un superadministrador puede **reiniciar** la clave de una
  tienda desde el portal («Datos de las tiendas» → «Reiniciar clave», auditado `SYNC_KEY_RESET`): queda sin clave hasta su próxima
  sincronización en línea.
- En la tienda, `sync-signing.key` se escribe de forma atómica (temporal + reemplazo). Si no se puede leer (DPAPI de otra máquina tras
  restaurar en otro equipo, archivo dañado) se genera una clave nueva con una advertencia en el log, en vez de fallar en cada ciclo: la
  nube la verá como rotación.
- Al cargar un paquete en el portal: primero se lee el ENCABEZADO (en claro, autenticado por el cifrado) y se revisan la instalación y el
  acceso del usuario a su empresa ANTES de descifrar y descomprimir. Si la instalación tiene clave registrada, el paquete DEBE venir
  firmado con ella (o con la anterior en sus 7 días); si no la tiene, se aplica y el lote queda `UNVERIFIED`, con la clave que lo
  firmó (`signer_key`). El portal muestra los lotes sin verificar por sucursal; con la permisión `sync.package.review`
  (superadministrador) se pueden **verificar** (cuando la tienda registra su clave y es la que firmó) o **descartar** (se borran de
  `sync.documents` los documentos que dejó ese lote y que ningún lote posterior reescribió: `documents.batch_id`). Único `DELETE` de
  `pos_app` en la nube.
- `FindAsync` exige licencia activa e instalación no liberada; el portal dice por qué no carga (equipo liberado, licencia vencida o
  revocada, o instalación desconocida).
- Cargas de paquetes: ⚙️ 10 por usuario cada 10 minutos (`Sync:MaxUploadsPer10Minutes`).
- `version` se acota a `now() + 10 min` al aplicar: una versión futura ya no congela el documento.

### S3 · Bomba gzip

- `SyncPackage.Open` limita: ítems del encabezado (antes de descifrar) y del lote, contados MIENTRAS se leen (convertidor que recorre la
  lista dato por dato, `SyncLimits.MaxPackageItems` = 200 000), cada dato a 1 MiB (`MaxItemBytes`) y los bytes descomprimidos a 128 MiB
  (`MaxPackageBytes`) con un flujo que cuenta. La tienda arma paquetes de hasta el 90 % de ese tamaño (si no caben, exporta menos datos y el
  resto sale en el paquete siguiente). Los lotes en línea también rechazan datos de más de 1 MiB. La nube aplica en bloques de 5 000.

### Bajas

- `ServerUrl` con `http://` se rechaza (sincronización no configurada) salvo `localhost`/loopback fuera de producción.
- `ProtectedSecret` fuera de Windows: documentado en el código y en este archivo (§4).

## 2. Pagos en línea (Wompi, nube)

- **W1.** Si la consulta a Wompi falla, el webhook responde `503` (`PAYMENTS.GATEWAY_UNAVAILABLE`) y Wompi reintenta; el evento queda
  registrado igual (el resultado `Outcome` confirma la transacción). La conciliación también revisa las órdenes `DECLINED`/`EXPIRED`
  de los últimos 7 días (un pago aprobado después igual se aplica); para esas solo se registra la consulta si algo cambió.
- **W2.** Una orden pendiente solo vence si la pasarela NO la reporta `PENDING`.
- **W3.** Renovación (manual y en línea): vigente → el periodo continúa desde el fin del actual; en gracia o vencida → empieza HOY.
  (Prueba: empieza hoy, como antes.) En la tienda, «Ya pagué, verificar» compara la fecha de vigencia antes y después de verificar
  (el pago aplicado es la nueva fecha que trae el token); `PaymentUrl == null` ya no se anuncia como «Pago recibido» si la
  suscripción está suspendida/cancelada o no cambió la fecha. Detectar por el id de la orden exige que el check-in devuelva la
  última orden aplicada y que la tienda la guarde (módulo de licencias de la tienda): queda pendiente para quien trabaja ahí.
- **Firma de integridad con `expiration-time`.** El checkout se firma con `referencia + centavos + moneda + expiración + secreto`
  (orden que ya implementaba `WompiProtocol.IntegritySignature`) y envía `expiration-time` (ISO 8601 UTC) = creación + plazo de
  vencimiento de la orden.
- **Firma inválida.** Se guardan solo metadatos (tipo, longitud y SHA-256 del cuerpo, motivo, IP de origen), nunca el cuerpo; la
  auditoría `PAYMENT_EVENT_REJECTED` se escribe a lo sumo una vez cada 10 minutos por IP, y se guardan a lo sumo 100 filas por IP por
  hora (después solo se responde 401).
- **Concurrencia.** `PaymentApplier` bloquea la suscripción (`SELECT … FOR UPDATE`) antes de renovar o revertir.
- **Antirreplay.** Evento con `timestamp` fuera de la tolerancia (72 h hacia atrás, 5 min hacia adelante) se rechaza como firma
  inválida.
- **/pagar/resultado.** La redirección es `/pagar/resultado/<referencia>/<token>` con 128 bits aleatorios por orden
  (`payment_orders.result_token`). Sin token correcto la página solo dice «esperando confirmación» (sin valor ni estado).
- **VOIDED** de un pago ya aplicado revierte la extensión: la orden guarda el periodo concedido y el anterior; si sigue siendo el
  último periodo se restaura el anterior, si no se descuenta la duración concedida; el fin (y con él la gracia) nunca queda antes del
  inicio. Evento `RENEWAL_REVERTED` en la suscripción. Si la suscripción está cancelada, queda «Requiere revisión» como antes.

## 3. Backups en S3 (R4, documentación)

La clave del objeto ya es por instalación: `[prefijo/]<id-instalación>/<archivo>.posbak` (ahora en `S3Destination.ObjectPrefix`, que
además limpia barras y segmentos `.`/`..` del prefijo, con prueba). La guía (despliegue-nube.md §14) pide configurar el prefijo del destino
como `<NIT>-<sucursal>` y una política por tienda limitada a `pos-backups/<NIT>-<sucursal>/*` sin `Delete*`, con bucket versionado y
object lock (GOVERNANCE 30 días), e incluye la política JSON y la nota de que el encabezado del `.posbak` va en claro. Como la tienda ya no
puede borrar, la retención en S3 la hace el ciclo de vida del bucket: un `403 AccessDenied` al borrar se trata como «lo vence el bucket»
(advertencia en el log y la copia se marca eliminada en la tienda) y la prueba del destino no falla por no poder borrar el archivo de
prueba. Un `403` por credenciales o reloj (`InvalidAccessKeyId`, `SignatureDoesNotMatch`, `RequestTimeTooSkewed`) es un error y la copia
NO se marca. Las subidas llevan `Content-MD5` (S3/MinIO lo exigen para un PUT en un bucket con object lock). La guía explica cómo migrar
las tiendas que tenían `readwrite` y prefijo vacío.

## 4. ProtectedSecret fuera de Windows

DPAPI solo existe en Windows. Fuera de Windows (pruebas en Linux, desarrollo) `ProtectedSecret.Reveal` falla con valores `dpapi:` y
las claves de archivo (backup, firma de sincronización) se guardan EN CLARO: su protección es solo la de los permisos del sistema de
archivos. El producto se instala en Windows; no hay despliegue de tienda fuera de Windows.
