# ADR-0049 · Datos personales enmascarados y retención de la bitácora

- **Estado:** Aceptada · 2026-09-29 · Fase 10 · Decisiones D10-07 y D10-08

## Contexto
La bitácora es evidencia y no se puede modificar ni borrar (ADR-0012). La Ley 1581 da al titular el derecho a la supresión de sus datos.
Si la bitácora guardara el correo, el teléfono o la dirección de un cliente, quedarían para siempre en un registro imborrable.

## Decisión
- **Atributo `[PersonalData(tipo)]`** en las propiedades con datos personales; el interceptor de auditoría guarda solo una versión
  enmascarada: correo `j***@dominio.com`, teléfono `***1234` (últimos 4 dígitos), texto libre (dirección, notas, detalle de una solicitud
  de titular) como `(registrado)`. Se aplica a terceros y sus contactos, empleados, usuarios (correo) y solicitudes de titulares.
- La **identificación y el nombre** se conservan completos: son la evidencia de quién es el titular del cambio. Cuando se suprime un
  cliente, la entidad se anonimiza (Fase 8) y la bitácora conserva solo lo mínimo.
- El enmascarado es fijo (no configurable): lo sellado no se puede re-enmascarar, así que un interruptor solo agregaría riesgo.
- **Retención:** la bitácora **nunca se borra** en la tienda; mínimo 10 años (libros y papeles del comerciante). Las particiones
  mensuales antiguas se archivarán con el backup (Fase 11).

## Consecuencias
- Investigar un cambio de correo muestra solo la inicial y el dominio; el valor completo está en la entidad (mientras exista).
- Lo registrado antes de la Fase 10 queda como estaba.
- Desviación respecto a la propuesta: el ⚙️ de D10-07 no se implementó como configuración (motivo arriba).
