# Endurecimiento de la licencia de la tienda — Diseño breve

- **Estado:** Implementado en la rama `fix-licencia` — pendiente de tu validación · 2026-10-17
- **Origen:** revisión de seguridad del licenciamiento (`src/Modules/Licensing` y relacionados), hallazgos R1–R3, H3–H9, N1.
- **Base:** [fase-12b-propuesta.md](fase-12b-propuesta.md) · ADR 0053 y 0054.
- **Migración:** `V2026.10.090__licensing__state_mac.sql` (incluye el *trigger* que hace inmutable `system.installation.created_at`).

## 1. Principios

1. **Lo que cambia la licencia nunca sale de una variable del equipo.** El entorno (`DOTNET_ENVIRONMENT`) lo controla cualquier
   administrador de Windows; las puertas de desarrollo solo existen si el binario se compiló en `Debug`.
2. **La única hora que puede BAJAR la marca de tiempo es la de la nube** (el `iat` de un token firmado y verificado para este equipo).
   El reloj local y el tiempo de funcionamiento solo la pueden subir.
3. **El estado local sensible va firmado con un secreto de la instalación** que no está en la BD; si no cuadra, la licencia queda
   restringida hasta que la nube lo confirme (check-in o activación).
4. **Ante la duda, restringir con un mensaje claro**, nunca quedar libre para siempre; pero restringir NO bloquea cerrar la jornada,
   respaldar, exportar ni verificar la licencia.

## 2. Decisiones por hallazgo

| Punto | Decisión |
|---|---|
| R1 | `DevelopmentTrustedKeys`/`DevelopmentFingerprint` (licencia) y `Pos:Updates:DevelopmentTrustedKeys` (actualizador) se leen solo con `#if DEBUG` **y** entorno distinto de producción. En `Release` el código que las lee no existe. El proveedor fiscal `FAKE` por configuración, igual: solo en `Debug`. Las pruebas de integración ya no usan la configuración: reemplazan `ILicenseKeys`, `IDeviceIdentity` e `IFiscalProvider` por inyección (`ConfigureTestServices`), que funciona en `Debug` y en `Release` y que nadie puede hacer desde fuera del proceso. |
| R2 | Al guardar un token verificado (activación o check-in) `max_observed_utc := max(iat, ahora corregido)` — se corrige aunque baje. Así un reloj adelantado por error se arregla con el siguiente check-in. |
| R3 | `CorrectFiscalBuyerCommand` pasa a `IAllowedWhenRestricted` (sin corregir el adquirente la cola DIAN no avanza). |
| H3 | La marca avanza también con el **tiempo de funcionamiento** (reloj monótono del proceso): en cada recálculo del proceso de fondo `marca := max(marca + transcurrido, ahora corregido)`, con el transcurrido acotado a 15 min por ciclo (contar de menos es seguro). Atrasar el reloj poco a poco ya no congela la hora: la marca sigue y al pasar 24 h de diferencia se detecta el retroceso (y los vencimientos se calculan con la marca). Equivale a "inicio + minutos acumulados" sin columnas nuevas: la marca ES el acumulado persistido. |
| H4 | **(a) MAC**: `state_mac` = HMAC-SHA256 de (nodo, token, revoked, reactivation_required, deactivated, clock_offset_seconds, max_observed_utc, last_checkin_at, restricted_since, demo_started_at, offline_floor_utc) con un secreto de 32 bytes de la instalación guardado en `{DataRoot}\config\license.vault` protegido con DPAPI de la máquina (como `backup.key`). Si no cuadra → `RESTRICTED` con la ventana normal de 24 h (en demo no restringe; ver §5) hasta el próximo check-in/activación (restringida, el proceso de fondo verifica al arrancar y luego al menos cada hora). El recálculo de cada minuto NO vuelve a firmar un estado alterado; solo lo hacen las escrituras confirmadas por la nube (token nuevo, equipo liberado). Filas anteriores sin MAC se adoptan solo si el archivo todavía no ha sellado nada. **(b)** Un token nuevo con `iat` anterior al vigente se rechaza. **(c) Backups**: en vez de excluir el esquema, el mismo archivo guarda la marca de tiempo más alta vista (`watermark`) y al leer se usa `max(BD, archivo)`: restaurar un backup viejo no retrocede la marca. Es lo más simple (no toca el motor de backups) y cubre también ediciones directas de la BD. |
| H5 | El caché del estado guarda cuándo se calculó (reloj monótono). Si no hay un cálculo exitoso en **1 hora** (o nunca lo hubo una hora después de arrancar), el filtro pasa a `RESTRICTED` con el mensaje "No se pudo calcular el estado de la licencia…". |
| H6 | `licensing.license_state.restricted_since` (hora confiable en que pasó a restringida). Durante **24 h** desde ahí se puede seguir vendiendo en la jornada abierta; después solo se cierra. Los comandos de venta llevan la marca nueva `IRestrictedSalesWindow` (prueba R10): iniciar, agregar línea, cambiar cantidad, precio abierto, descuento, cliente, suspender, recuperar, cobrar (`CompleteSale`), cambio, devolución por garantía e iniciar pago Sistecrédito. Cancelar, anular, quitar descuento, reimprimir, verificar/cancelar pagos ya iniciados y todo lo del cierre siguen permitidos. Una venta en curso al cumplirse las 24 h ya no se puede cobrar: se cancela. Con el estado alterado (MAC) la ventana es la normal de 24 h, contada desde la hora que guarda el archivo (§5). |
| N1 | Máximo de días sin check-in exitoso: **30 (PENDIENTE DE APROBACIÓN)**, `LicenseEvaluator.DefaultMaxOfflineDays`. Se cuenta desde el `iat` del token vigente (firmado: editar la BD no lo alarga). `Pos:Licensing:MaxOfflineDays` solo puede ACORTARLO (1–30). `RefreshAfter` no aplica como límite (es la sugerencia de cuándo renovar; el check-in diario ya la cumple). |
| Permitidos | Se agregan `SetProviderCredentials`, `UpdateBillingSettings`, `CreatePairingCode`, `PairDevice`, `RevokeDevice`, `SetReceiptPrinter`. Se quitan `CreateExpense`, `VoidExpense` y `RebuildStock`. Riesgos en §3. |
| H7 | El NIT del token se compara con el de la empresa (número base y, si ambos lo tienen, dígito de verificación). Distinto → la activación/check-in se rechaza y el estado es `RESTRICTED`. |
| H9 | **Pendiente (documentado)**: ver §4. |

## 3. Riesgo de los comandos agregados a la lista de permitidos

- **SetProviderCredentials / UpdateBillingSettings**: permiten cambiar credenciales y modo/ambiente de la facturación con la licencia
  restringida. Riesgo bajo: no venden ni abren jornadas, quedan auditados, y bloquearlos dejaría documentos fiscales de ventas ya
  hechas sin enviar a la DIAN (riesgo legal para la tienda). Apagar la facturación (`OFF`) en restringida no da ningún beneficio.
- **CreatePairingCode / PairDevice**: permiten emparejar un equipo nuevo. Es el riesgo más alto de la lista (sumar cajas mientras no se
  paga), acotado porque abrir jornadas sigue bloqueado: un equipo nuevo no puede vender. Se permite para reemplazar una caja dañada
  y poder cerrar su jornada desde otro equipo.
- **RevokeDevice**: acción de seguridad (equipo robado o dañado); bloquearla sería peor.
- **SetReceiptPrinter**: sin impresora no salen el cierre ni los tiquetes; no otorga funcionalidad nueva.
- **Quitados**: `CreateExpense`/`VoidExpense` (el dinero que sale del cajón se registra con `RegisterMovement`, que sigue permitido) y
  `RebuildStock` (mantenimiento administrativo; `VerifyStock` sigue permitido).

## 4. Límites conocidos y pendientes

- Un administrador de Windows puede leer el secreto DPAPI de máquina, borrar el archivo o modificar el binario: la MAC protege contra
  ediciones de la BD (credenciales de `pos_app`, restauraciones), no contra el dueño del equipo con permisos totales. Borrar el archivo
  con la BD sellada deja la licencia restringida hasta el siguiente check-in.
- **H9 (demo al reinstalar)**: si al desinstalar se conservan los datos, la reinstalación reutiliza la BD y la demo NO se reinicia. Si
  se borran los datos, `C:\ProgramData\BusinessPost` desaparece (con el archivo de la licencia) y la demo empieza de nuevo. Arreglarlo
  bien exige registrar la demo en la nube por huella del equipo (o un marcador en el registro de Windows fuera de la carpeta de datos);
  no es trivial y queda pendiente de tu decisión.
- El `BaseUrl` de Factus por configuración fuera de producción y la clave de sincronización de desarrollo siguen dependiendo del
  entorno (no son de licencia); quedan anotados para una revisión aparte.

## 5. Ronda 2 (revisión de la primera implementación)

| Punto | Decisión |
|---|---|
| Vuelta atrás / backup anterior | El archivo da por **sellado** el estado solo al LEER una fila firmada, válida y ya confirmada por otra transacción (`xmin` distinto de la transacción en curso): un primer sellado revertido no deja el archivo sellado. Un estado alterado restringe con la **ventana normal de 24 h** (no cierra la venta al instante). En **demo** (sin token) un estado alterado no restringe: se evalúa con la marca y el inicio de la demo reconciliados con el archivo. |
| Demo que se alarga | `licensing.license_state.demo_started_at` (copia firmada de la creación de la instalación); el archivo guarda el MENOR inicio confirmado y al leer se usa `min(instalación, copia firmada, archivo)`. El *trigger* `system.fn_installation_protect` (migración 090) impide cambiar `system.installation.created_at`. |
| Lectura del archivo | Errores de IO: 3 reintentos y, si siguen, se trabaja sin el archivo hasta reiniciar SIN sobrescribirlo (no se firma nada; las firmas no se pueden comprobar → restringida con la ventana de 24 h). Solo un archivo ilegible de verdad (DPAPI, JSON, base64 o secreto que no mide 32 bytes) se aparta como `.bad` y se crea uno nuevo. |
| Fila vieja vuelta a escribir | El archivo guarda la mayor emisión (`iat`) de un token confirmado, la primera hora de la restricción y los indicadores de la nube (revocada, reactivación, liberado). Un token guardado más viejo que el confirmado es una alteración; `restricted_since` se toma del archivo (no se reinicia la ventana) y los indicadores se suman (OR) hasta el próximo token nuevo. Restaurar un backup legítimo queda restringido con la ventana de 24 h hasta el check-in. |
| Otra huella + alterado | Se evalúa la huella antes que la firma: con la BD en otro PC el mensaje es el de reactivar con la clave (`REACTIVATION_REQUIRED`, ventana de 24 h). |
| N1 al actualizar | `offline_floor_utc` (migración 090 lo pone en la hora de la actualización para las instalaciones existentes; firmado y guardado en el archivo la primera vez que se confirma): los días sin conexión cuentan desde `max(iat, piso)`. Desde el día 23 (7 días antes del límite) aparece el aviso `OFFLINE_LIMIT` en los avisos de la administración (Tablero). |
| Concurrencia | La lectura previa a cada escritura firmada usa `SELECT … FOR UPDATE`. |
| Reinicios (9) | El tiempo de funcionamiento se mide entre dos recálculos del MISMO proceso: el primero tras arrancar no suma nada. Si el servicio se reinicia cada menos de 2 minutos, la marca solo avanza con el reloj (lo de H3 no aplica). Es conservador (cuenta de menos) y un reinicio así de seguido ya es visible en el registro de Windows; queda documentado. |
| Stale (10) | Sin cálculo exitoso en 1 h el filtro restringe y **conserva la última decisión de venta** (si ya no se podía vender, sigue sin poderse; si se podía, la jornada abierta sigue vendiendo). Sin ningún cálculo desde el arranque, la venta queda abierta. |

Nota: la migración 090 se amplió en esta ronda (no está publicada en `main`). Una BD de desarrollo que ya la tuviera aplicada debe recrearse.
