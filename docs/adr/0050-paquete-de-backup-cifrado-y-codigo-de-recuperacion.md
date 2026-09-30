# ADR-0050 · Paquete de backup cifrado y código de recuperación

- **Estado:** Aceptada · 2026-09-29 · Fase 11 · Decisiones D11-01 a D11-04

## Contexto
Los backups deben poder copiarse a cualquier lugar (USB, red, nube) sin que quien los tenga pueda leerlos, restaurarse en otro equipo si
el servidor se pierde y detectar cualquier alteración.

## Decisión
- **Volcado con `pg_dump`** (formato custom, comprimido, sin dueños ni privilegios) con el rol `pos_backup`, **dentro de una foto
  exportada** (`pg_export_snapshot`): el volcado, los conteos de control y el último sello de auditoría del encabezado son exactamente
  del mismo momento. Restauración con `pg_restore` (`--single-transaction`, `--disable-triggers`, a nombre del dueño del esquema).
- **Paquete `.posbak` v1**: `"POSBAK1\n"` · encabezado JSON legible (formato, versión de app y esquema, NIT, sucursal, nodo y su vida,
  fecha, tipo, sello de auditoría, conteos, SHA-256 del contenido, id de la clave, clave envuelta) · bloques de 1 MiB cifrados con
  **AES-256-GCM**. Los datos autenticados de cada bloque incluyen el SHA-256 del encabezado, el número de bloque y la marca de último
  bloque: cambiar el encabezado, alterar, reordenar o truncar el archivo se detecta. El contenido es un ZIP con el volcado, la
  configuración de la instalación **sin secretos** y un manifiesto con el SHA-256 de cada parte.
- **Clave de datos** de 32 bytes por instalación, guardada en `{DataRoot}\config\backup.key` protegida con **DPAPI de la máquina**;
  nunca en la BD.
- **Código de recuperación** de 24 caracteres (Crockford base32, 120 bits) que el propietario imprime; se muestra **una sola vez**. La
  clave de datos se envuelve con él (Argon2id 64 MiB, 3 pasadas + AES-256-GCM) y esa envoltura va en la BD (`backup.recovery_keys`) y en
  el encabezado de cada backup. El propietario confirma que lo guardó **escribiéndolo de nuevo** (se comprueba abriendo la envoltura).
  Regenerarlo no cambia la clave de datos: los backups nuevos llevan la envoltura nueva.

## Consecuencias
- Un archivo robado no sirve; ni el VPS ni nosotros podemos leer los backups de la nube.
- **Perder el código impide restaurar en otro equipo** (en el mismo equipo basta la clave local): de ahí la confirmación y la alerta.
- El formato lleva versión: cualquier backup viejo debe poder leerse siempre.
- Desviación: la propuesta decía confirmar con los últimos 8 caracteres; se confirma con el código completo porque solo así se puede
  comprobar sin guardar el código.
