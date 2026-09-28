# ADR-0003 · PostgreSQL 18 en todos los planes

- **Estado:** Aceptada · 2026-09-28 (aprobada por el propietario del producto)

## Contexto
Varias cajas escriben concurrentemente contra la BD de la tienda. SQLite compartido por red se corrompe; mantener dos motores (SQLite para una caja, PostgreSQL para varias) duplica pruebas y errores.

## Decisión
- **PostgreSQL 18** como motor único, incluido el plan de una caja.
- En los clientes, se instala desde **binarios empaquetados** por nuestro instalador: puerto no estándar, escucha solo en `localhost` y usuario de aplicación con privilegios mínimos.
- En desarrollo y pruebas corre en **Docker** (Testcontainers), a partir de la Fase 2.
- SQLite se reserva para cachés locales del terminal (modo offline futuro).

## Consecuencias
- ✅ Transacciones y bloqueos robustos, `numeric` exacto, índices parciales/trigramas, JSONB, DDL transaccional (migraciones seguras) y `uuidv7()` nativo.
- ✅ El mismo motor sirve para la futura consolidación en la nube.
- ⚠️ El instalador debe gestionar un servicio adicional (`PosSupermercado-DB`). Ver riesgo R-04.
