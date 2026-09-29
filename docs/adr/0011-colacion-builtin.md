# ADR-0011 · Colación `builtin C.UTF-8` y orden español explícito

- **Estado:** Aceptada · 2026-09-28 · Fase 2

## Contexto
La colación por defecto de una BD PostgreSQL no se puede cambiar sin recrearla. Las colaciones de Windows o ICU cambian con las actualizaciones del sistema y pueden dejar **índices inconsistentes** en los equipos de los clientes.

## Decisión
- `create-database` crea la BD con `LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8'` (PostgreSQL 17+): estable ante actualizaciones.
- El orden alfabético en español (ñ, tildes) se aplica **solo donde se muestra al usuario**, de forma explícita: `ORDER BY name COLLATE public.es_co` (colación ICU creada en la línea base).
- Búsquedas sin tildes con `unaccent`.

## Consecuencias
- ✅ Índices estables durante toda la vida de la instalación.
- ⚠️ Hay que recordar `COLLATE public.es_co` en las consultas que ordenan nombres (se centraliza en los modelos de lectura).
