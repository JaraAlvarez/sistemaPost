# Notas de versión

## 1.2.0: cómo llevarla a las tiendas existentes (1.0.x / 1.1.0)

### Orden obligatorio
1. **Primero la nube.** Despliegue la nube nueva (migraciones `V2026.10.010` a `V2026.10.015` sobre la `009` que hay en producción) y
   compruebe `/health`, el portal y que la 015 quedó en `system.schema_migrations` (docs/despliegue-nube.md §10). Una tienda 1.2.0 contra
   la nube vieja no sincroniza (paquetes firmados, cabeceras de la clave de firma).
2. **Después las tiendas, con el instalador.** La 1.2.0 se lleva a las tiendas que ya están en 1.0.x o 1.1.0 ejecutando
   `BusinessPost-Setup-1.2.0.exe` en el equipo principal (con las cajas cerradas). El instalador hace el backup PRE_UPDATE, migra y, si
   algo falla, vuelve solo a los datos de antes (códigos 3/8 del migrador, docs/guia-recuperacion.md).
3. **No se publica el manifiesto `stable.json` de la 1.2.0 para la flota vieja** hasta validar en la VM la actualización automática
   desde una 1.1.0 real (con una migración que falle a propósito y con una que salga bien). Mientras tanto, la 1.2.0 solo entra por el
   instalador.

### Si una tienda 1.0.x/1.1.0 recibe la 1.2.0 por actualización automática
El primer salto lo hace el actualizador VIEJO (el de la versión instalada): backup PRE_UPDATE con su migrador, detiene los servicios y
llama al migrador 1.2.0 con `migrate --no-backup`. Ese actualizador, si la migración falla, solo vuelve a arrancar el servidor anterior
y escribe en el historial *"Falló la migración de la base de datos; no se aplicó ningún cambio y sigue la versión anterior"*. **Ese
texto es del actualizador viejo y no se puede cambiar desde la 1.2.0, y no es exacto:** cada script de migración va en su propia
transacción, así que sin protección la base quedaba a medio migrar y el servidor anterior en `SCHEMA_OUTDATED`.

Desde la 1.2.0 el **migrador** se protege solo cuando lo llama un actualizador viejo (sin `updates\update-in-progress.json` del
actualizador nuevo): hace su propio backup PRE_UPDATE (ya con el servidor detenido) y, si una migración falla, lo restaura en una base
nueva, cambia `server.json` a ella y termina con el código **9**. El servidor anterior vuelve a arrancar sobre una base sana: lo que
realmente pasó es *"falló la migración y se volvió a los datos de antes (backup PRE_UPDATE restaurado)"*. Si tampoco pudo volver atrás,
termina con **8** (restaurar con soporte). Detalle y códigos en docs/guia-recuperacion.md.

**Reintentos cada noche:** el actualizador viejo no guarda la versión fallida y vuelve a intentarla en la siguiente ventana (02:00). El
migrador 1.2.0 deja la marca `updates\legacy-migrate-failed.json` y, si se lo vuelve a llamar con la misma versión sobre los mismos datos,
termina con **10** sin hacer backup ni tocar la base. Lo que NO se puede evitar desde la 1.2.0: cada noche el actualizador viejo vuelve a
hacer su backup (en `updates\backups\<fecha>`, ocupa disco) y detiene y arranca los servicios unos minutos (sin jornadas abiertas). Se
corta instalando la 1.2.0 con el instalador (o una versión siguiente). Soporte puede forzar otro intento con `--retry-failed`.

### Cajas adicionales
- **Actualice las cajas adicionales el mismo día que el equipo principal** (instalador en modo Caja). Una caja 1.1.0 contra un
  servidor 1.2.0 sigue vendiendo, pero la **báscula** necesita el agente 1.2.0: mientras la caja no se actualice, los productos por peso
  se venden con **peso digitado y autorización de supervisor** ("permitir digitar el peso" en Configuración → Cajas e impresoras →
  Báscula).
- El aviso de "cajas desactualizadas" del Tablero usa la versión que informa el actualizador de cada caja al pedir el manifiesto, y eso
  solo lo hace el actualizador 1.2.0. Una caja 1.1.0 no informa su versión por ningún otro camino (no tiene latido, y el emparejamiento
  guardó la versión de la página web del servidor, no la de la caja): aparece como "versión desconocida" o con la versión del
  emparejamiento hasta que se actualice. Firme la 1.2.0 con `--min-terminal 1.2.0` para que el aviso las señale.

### Licencia
- Después de una vuelta atrás o de restaurar un backup la licencia puede quedar **RESTRINGIDA "alterada"** sin jornadas abiertas. Con
  ese motivo (o con el archivo de la licencia ilegible) ahora **también se puede abrir caja** durante la ventana de 24 h, además de
  vender; pasada la ventana, no. Con un motivo de pago, vencimiento o revocación abrir caja sigue bloqueado.
- Permitidos con la licencia restringida (R10): rotar y verificar las credenciales del proveedor de facturación, asignar un rango de
  numeración, apagar Sistecrédito y resolver un pago en revisión, **desactivar** usuarios (activar sigue bloqueado) y configurar la
  báscula de la caja.
- **Garantía con devolución de dinero** (`WarrantyRefund`): permitida también después de las 24 h (es un egreso, nunca un ingreso).
  **Cambio por otro producto** (`StartExchange`): solo dentro de la ventana de 24 h, porque termina en una venta nueva y puede generar
  ingreso (diferencia a favor de la tienda); pasada la ventana se hace la garantía con devolución.

## Rama `pantallas-admin` (pendiente de unir a `main`)

### Nuevo
- **Configuración → Backups:** carpeta de los backups del servidor, último backup y su resultado, "Respaldar ahora" y destinos de disco
  externo o carpeta de red, con la carpeta comprobada en el servidor antes de guardar.
- **Facturación → Configuración → Factus:** conectar Factus desde la pantalla ("Conectar", "Volver a probar", "Cambiar llaves",
  "Desconectar"), documentos retenidos con "Enviar ahora" / "No enviar", rangos por ambiente.
- El adaptador por defecto es **FACTUS** (adenda A del ADR-0059, aprobada). La facturación nace apagada (OFF) y no envía nada sin una
  conexión verificada. Para prohibirla en una instalación: `"Pos": { "Billing": { "Provider": "NONE" } }` en `server.json`.

### Base de datos
- Migración **V2026.10.060** (`billing__provider_verification`): verificación de la conexión, atraso retenido, ambiente por documento y
  por rango de numeración, estado `NOT_SENT`.
- **Aviso para desarrollo:** la V060 se corrigió varias veces dentro de la rama y **cambió de checksum**. Una BD de desarrollo que ya
  aplicó una versión anterior de la V060 queda bloqueada por el migrador (script modificado). Recréala (`dev-db.ps1`) o borra su fila:
  `DELETE FROM system.schema_migrations WHERE script_name LIKE 'V2026.10.060__%';` y deshaz a mano lo que esa versión creó antes de migrar
  de nuevo. **En producción no hay ninguna**: la V060 no se ha desplegado.
- Al actualizar, una empresa con la facturación electrónica encendida sin verificación queda **apagada** y el tablero lo avisa.
